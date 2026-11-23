using System.Globalization;
using System.Net;
using System.Security.Claims;
using BlueMail.Application.Common;
using BlueMail.Application.Web;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Security;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BlueMail.Api.Auth;

/// <summary>The validated session of the request, stored in HttpContext.Items by token validation.</summary>
public static class SessionItem
{
    private const string Key = "bm.session";

    public static void Set(HttpContext http, SessionState state, Guid sessionId) => http.Items[Key] = (state, sessionId);

    public static (SessionState State, Guid SessionId)? Get(HttpContext http) =>
        http.Items.TryGetValue(Key, out var v) && v is (SessionState s, Guid id) ? (s, id) : null;
}

public static class ApiAuthentication
{
    public const string PermissionPolicyPrefix = "perm:";

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, BmSettings settings, OidcKeys keys)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.IdentityUrl + "/",
                ValidAudience = OidcKeys.ApiAudience,
                IssuerSigningKey = keys.Signing,
                ValidateIssuerSigningKey = true,
                ValidTypes = ["at+jwt", "JWT"],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "sub",
            };
            o.Events = new JwtBearerEvents
            {
                // Browsers cannot set headers on WebSocket requests: SignalR sends the token as access_token, only for the hub.
                OnMessageReceived = ctx =>
                {
                    if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"] is { Count: 1 } t)
                    {
                        ctx.Token = t[0];
                    }

                    return Task.CompletedTask;
                },
                // PROMPT 8.1 / 14.9: "may this holder still get in" is decided here, once, for every endpoint.
                OnTokenValidated = async ctx =>
                {
                    var p = ctx.Principal!;
                    if (!Guid.TryParse(p.FindFirstValue("sub"), out var userId) ||
                        !Guid.TryParse(p.FindFirstValue(OidcKeys.SessionClaim), out var sessionId) ||
                        p.FindFirstValue(OidcKeys.StampClaim) is not { } stamp)
                    {
                        ctx.Fail("token_incomplete");
                        return;
                    }

                    var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                    var state = await validator.ValidateAsync(userId, sessionId, stamp, ctx.HttpContext.RequestAborted);
                    if (!state.IsValid)
                    {
                        ctx.Fail(state.Rejection.ToString());
                        return;
                    }

                    ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>().Set(state.TenantId, state.UserId);
                    SessionItem.Set(ctx.HttpContext, state, sessionId);
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    await Problems.WriteAsync(ctx.HttpContext, HttpStatusCode.Unauthorized, "unauthenticated");
                },
                OnForbidden = ctx => Problems.WriteAsync(ctx.HttpContext, HttpStatusCode.Forbidden, "permission_denied"),
            };
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }
}

/// <summary>Endpoint needs the given permission (checked on the server whatever the UI shows, PROMPT 7).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute(ApiAuthentication.PermissionPolicyPrefix + permission)
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Endpoint open to any signed-in user that guards its own resource (e.g. "my sessions", "my mailbox" through
/// <c>IMailStore.OpenAsync</c>). The reason is mandatory and reviewed by the endpoint scan test.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SelfGuardedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(ApiAuthentication.PermissionPolicyPrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[ApiAuthentication.PermissionPolicyPrefix.Length..])).Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

internal sealed class PermissionHandler(IHttpContextAccessor http) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var s = http.HttpContext is { } h ? SessionItem.Get(h) : null;
        if (s is { } session && Permissions.For(session.State.IsPlatformAdmin, session.State.IsTenantAdmin).Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private (SessionState State, Guid SessionId)? Session => accessor.HttpContext is { } h ? SessionItem.Get(h) : null;

    public bool IsAuthenticated => Session is not null;
    public Guid UserId => Session?.State.UserId ?? Guid.Empty;
    public Guid TenantId => Session?.State.TenantId ?? Guid.Empty;
    public Guid SessionId => Session?.SessionId ?? Guid.Empty;
    public string Email => Session?.State.PrimaryEmail ?? "";
    public bool IsPlatformAdmin => Session?.State.IsPlatformAdmin ?? false;

    public IReadOnlySet<string> Permissions =>
        Session is { } s ? Application.Common.Permissions.For(s.State.IsPlatformAdmin, s.State.IsTenantAdmin) : new HashSet<string>();

    public string? Ip => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();
}

internal static class CultureList
{
    /// <summary>Every culture .NET knows is acceptable for formatting; UI strings fall back to English.</summary>
    public static readonly CultureInfo[] All = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Where(c => !string.IsNullOrEmpty(c.Name)).ToArray();
}
