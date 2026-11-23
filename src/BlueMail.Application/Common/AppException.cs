using System.Net;

namespace BlueMail.Application.Common;

/// <summary>
/// A business error with a stable code (PROMPT 0.2: clients translate by <see cref="Code"/>). The code is
/// also the localisation key "error.&lt;code&gt;" for the server-side title/detail.
/// </summary>
public class AppException(string code, HttpStatusCode status, IReadOnlyDictionary<string, object?>? args = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyDictionary<string, object?>? Args { get; } = args;

    public static AppException NotFound(string code = "not_found") => new(code, HttpStatusCode.NotFound);
    public static AppException Forbidden(string code = "forbidden") => new(code, HttpStatusCode.Forbidden);
    public static AppException Conflict(string code, IReadOnlyDictionary<string, object?>? args = null) => new(code, HttpStatusCode.Conflict, args);
    public static AppException BadRequest(string code, IReadOnlyDictionary<string, object?>? args = null) => new(code, HttpStatusCode.BadRequest, args);
}

public sealed record FieldError(string Field, string Code, IReadOnlyDictionary<string, object?>? Args = null);

public sealed class ValidationFailedException(IReadOnlyList<FieldError> errors)
    : AppException("validation_failed", HttpStatusCode.BadRequest)
{
    public IReadOnlyList<FieldError> Errors { get; } = errors;
}
