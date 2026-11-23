using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity store over identity.users. Sign-in happens before the tenant is known, so the
/// lookups by id / user name / email resolve across tenants (addresses are unique system-wide). They
/// still exclude soft-deleted users. Every other query goes through the normal tenant filter.
/// </summary>
public sealed class BlueMailUserStore(BlueMailDbContext context, IdentityErrorDescriber? describer = null)
    : UserStore<User, Role, BlueMailDbContext, Guid>(context, describer)
{
    private IQueryable<User> AllTenants => Context.Users.IgnoreQueryFilters().Where(u => u.DeletedAt == null);

    public override Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(userId, out var id)
            ? AllTenants.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            : Task.FromResult<User?>(null);

    public override Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default) =>
        AllTenants.FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);

    public override Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        AllTenants.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
}

/// <summary>
/// Normalises user names/emails the same way as <see cref="Domain.Common.EmailAddress"/> (NFC, lower case,
/// A-label domain) instead of Identity's default upper-casing.
/// </summary>
public sealed class EmailLookupNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => Normalize(name);

    public string? NormalizeEmail(string? email) => Normalize(email);

    private static string? Normalize(string? value) =>
        value is null ? null :
        Domain.Common.EmailAddress.TryParse(value, out var a) ? a.Value.Value : value.Trim().ToLowerInvariant();
}
