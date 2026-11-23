using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueMail.Application.Common;
using BlueMail.Infrastructure.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Web;

/// <summary>Success envelope of every API response (PROMPT 5).</summary>
public sealed record ApiResponse<T>(bool Success, T? Data, string Message, IReadOnlyList<object> Errors);

public static class ApiResponses
{
    public static ApiResponse<T> Ok<T>(T data) => new(true, data, "", []);
}

/// <summary>Error body: RFC 9457 problem details plus a stable <c>code</c> and field errors.</summary>
public sealed record ApiProblem(
    string Type, string Title, int Status, string? Detail, string Code, IReadOnlyList<ApiFieldError> Errors, string? TraceId, bool Success = false);

public sealed record ApiFieldError(string Field, string Code, string Message);

public static class Problems
{
    public static ApiProblem Build(HttpContext http, HttpStatusCode status, string code, IReadOnlyDictionary<string, object?>? args = null,
        IReadOnlyList<FieldError>? fields = null)
    {
        // The error middleware runs outside request localization: by the time an exception reaches it, the
        // thread culture is back to the default. The culture chosen for the request stays on the context (BUG-035).
        var culture = http.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()?.RequestCulture.UICulture ?? CultureInfo.CurrentUICulture;
        var title = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("error." + code, culture), culture, args);
        var errors = (fields ?? []).Select(f => new ApiFieldError(f.Field, f.Code,
            JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("error." + f.Code, culture), culture, f.Args))).ToList();
        return new ApiProblem("https://bluemail.dev/errors/" + code, title, (int)status, null, code, errors, http.TraceIdentifier);
    }

    public static Task WriteAsync(HttpContext http, HttpStatusCode status, string code, IReadOnlyDictionary<string, object?>? args = null,
        IReadOnlyList<FieldError>? fields = null)
    {
        http.Response.StatusCode = (int)status;
        http.Response.ContentType = "application/problem+json; charset=utf-8";
        return http.Response.WriteAsync(JsonSerializer.Serialize(Build(http, status, code, args, fields), WebJson.Options));
    }
}

public static class WebJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        o.Converters.Add(new NulStrippingStringConverter());
        return o;
    }
}

/// <summary>
/// PostgreSQL text cannot hold U+0000 (PROMPT 8.1, 14.22). Every string read from a JSON body passes here and
/// loses NUL characters at the door, before anything can try to store it.
/// </summary>
public sealed class NulStrippingStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is null || !s.Contains('\0', StringComparison.Ordinal) ? s : s.Replace("\0", "", StringComparison.Ordinal);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>Maps exceptions to problem responses. Registered after request logging (PROMPT 12).</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (ValidationFailedException ex) when (!http.Response.HasStarted)
        {
            await Problems.WriteAsync(http, ex.Status, ex.Code, ex.Args, ex.Errors);
        }
        catch (AppException ex) when (!http.Response.HasStarted)
        {
            await Problems.WriteAsync(http, ex.Status, ex.Code, ex.Args);
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
        {
            await Problems.WriteAsync(http, (HttpStatusCode)ex.StatusCode, "bad_request");
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to answer.
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            log.LogError(ex, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
            await Problems.WriteAsync(http, HttpStatusCode.InternalServerError, "internal_error");
        }
    }
}

/// <summary>Refuses query strings that carry U+0000 (raw or %00) with a clear 400.</summary>
public sealed class NulQueryFilterMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        foreach (var (_, values) in http.Request.Query)
        {
            foreach (var v in values)
            {
                if (v is not null && v.Contains('\0', StringComparison.Ordinal))
                {
                    return Problems.WriteAsync(http, HttpStatusCode.BadRequest, "invalid_characters");
                }
            }
        }

        return next(http);
    }
}

/// <summary>Strict security headers (PROMPT 8.1). The CSP has no 'unsafe-inline' for scripts.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, string contentSecurityPolicy)
{
    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = contentSecurityPolicy;
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (http.Request.IsHttps)
        {
            h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        return next(http);
    }
}

/// <summary>Culture of a request: user setting, then tenant default (from the validated session), then Accept-Language.</summary>
public sealed class SessionCultureProvider(Func<HttpContext, (string? UserLocale, string? TenantLocale)?> read) : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var locales = read(httpContext);
        var pick = locales?.UserLocale ?? locales?.TenantLocale;
        return Task.FromResult(pick is null ? null : new ProviderCultureResult(pick));
    }
}
