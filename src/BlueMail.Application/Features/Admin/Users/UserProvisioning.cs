using BlueMail.Application.Common;
using BlueMail.Domain.Common;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Admin.Users;

public sealed record NewUser(
    Guid TenantId,
    string Email,
    string DisplayName,
    string Password,
    bool IsTenantAdmin = false,
    bool IsPlatformAdmin = false,
    long? QuotaBytes = null,
    bool MustChangePassword = true,
    string? Locale = null,
    string? TimeZone = null);

/// <summary>Creates a user with its mailbox. Shared by the admin API, tenant creation and the seeder.</summary>
public sealed class UserProvisioning(BlueMailDbContext db, UserManager<User> users, IMailStore store, Policies.PolicyService policies)
{
    public const int MinPasswordLength = 10;
    public const long DefaultQuotaBytes = 30L * 1024 * 1024 * 1024;

    public async Task<User> CreateAsync(NewUser n, CancellationToken ct)
    {
        if (!EmailAddress.TryParse(n.Email, out var address))
        {
            throw new ValidationFailedException([new FieldError("email", ValidationCodes.InvalidEmail)]);
        }

        var email = address.Value;
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == n.TenantId && t.DeletedAt == null, ct)
                     ?? throw AppException.NotFound("tenant_not_found");
        if (n.Password.Length < await policies.IntAsync(n.TenantId, null, Policies.PolicyKeys.PasswordMinLength, ct))
        {
            throw new ValidationFailedException([new FieldError("password", ValidationCodes.PasswordTooWeak)]); // policy password_min_length
        }

        var domainOwned = await db.Domains.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == n.TenantId && d.Domain == email.Domain && d.DeletedAt == null, ct);
        if (!domainOwned)
        {
            throw AppException.BadRequest("domain_not_in_tenant", new Dictionary<string, object?> { ["domain"] = email.DisplayDomain });
        }

        if (tenant.UserLimit > 0)
        {
            var count = await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == n.TenantId && u.DeletedAt == null, ct);
            if (count >= tenant.UserLimit)
            {
                throw AppException.Conflict("user_limit_reached", new Dictionary<string, object?> { ["limit"] = tenant.UserLimit });
            }
        }

        if (n.IsPlatformAdmin && tenant.Kind != Domain.Tenants.TenantKind.Platform)
        {
            throw AppException.BadRequest("platform_admin_outside_platform_tenant");
        }

        var taken = await db.UserAliases.IgnoreQueryFilters().AnyAsync(a => a.Email == email.Value && a.DeletedAt == null, ct)
                    || await db.Groups.IgnoreQueryFilters().AnyAsync(g => g.Email == email.Value && g.DeletedAt == null, ct)
                    || await db.SharedMailboxes.IgnoreQueryFilters().AnyAsync(s => s.Email == email.Value && s.DeletedAt == null, ct);
        if (taken)
        {
            throw AppException.Conflict("email_taken");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = n.TenantId,
            UserName = email.Value,
            Email = email.Value,
            EmailConfirmed = true,
            PrimaryEmail = email.Value,
            DisplayName = n.DisplayName.Trim().Normalize(System.Text.NormalizationForm.FormC),
            IsTenantAdmin = n.IsTenantAdmin,
            IsPlatformAdmin = n.IsPlatformAdmin,
            QuotaBytes = n.QuotaBytes ?? DefaultQuotaBytes,
            MustChangePassword = n.MustChangePassword,
            Locale = n.Locale,
            TimeZone = n.TimeZone,
            LockoutEnabled = true,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, n.Password);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The unique index on primary_email is the real guard against two concurrent creations.
            throw AppException.Conflict("email_taken");
        }

        if (!result.Succeeded)
        {
            var dup = result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail");
            throw dup
                ? AppException.Conflict("email_taken")
                : new ValidationFailedException(result.Errors.Select(e => new FieldError("password", MapIdentityError(e.Code))).ToList());
        }

        await store.ProvisionMailboxAsync(n.TenantId, user.Id, MailboxOwnerKind.User, ct);
        await tx.CommitAsync(ct);
        return user;
    }

    private static string MapIdentityError(string code) => code switch
    {
        "PasswordTooShort" or "PasswordRequiresUniqueChars" or "PasswordRequiresNonAlphanumeric" or "PasswordRequiresDigit"
            or "PasswordRequiresLower" or "PasswordRequiresUpper" => ValidationCodes.PasswordTooWeak,
        _ => ValidationCodes.InvalidFormat,
    };
}
