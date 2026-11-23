using FluentValidation;
using MediatR;

namespace BlueMail.Application.Common;

/// <summary>Runs every FluentValidation validator of a request before its handler.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            errors.AddRange(result.Errors.Select(e => new FieldError(
                e.PropertyName.Length == 0 ? "" : char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..], // whole-object rules have no property
                e.ErrorCode,
                e.FormattedMessagePlaceholderValues?
                    .Where(kv => kv.Key is not ("PropertyName" or "PropertyValue" or "PropertyPath"))
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value))));
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return await next(cancellationToken);
    }
}

/// <summary>Stable error codes used by validators (localised as "error.&lt;code&gt;").</summary>
public static class ValidationCodes
{
    public const string Required = "required";
    public const string TooLong = "too_long";
    public const string InvalidEmail = "invalid_email";
    public const string InvalidFormat = "invalid_format";
    public const string PasswordTooWeak = "password_too_weak";
    public const string TooMany = "too_many";
}
