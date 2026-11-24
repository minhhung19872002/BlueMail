using BlueMail.Domain.Common;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Identity.Services;

/// <summary>
/// Terms and privacy links on the sign-in pages are configured per tenant (PROMPT 0.2). The tenant is found
/// from the domain of the address typed (or the OIDC login_hint); otherwise the platform tenant's links show.
/// </summary>
public sealed class TenantLinks(BlueMailDbContext db)
{
    public async Task ApplyAsync(ViewDataDictionary view, string? email, CancellationToken ct)
    {
        Tenant? tenant = null;
        if (EmailAddress.TryParse(email, out var address))
        {
            tenant = await db.Domains.IgnoreQueryFilters()
                .Where(d => d.Domain == address.Value.Domain && d.DeletedAt == null)
                .Join(db.Tenants.IgnoreQueryFilters(), d => d.TenantId, t => t.Id, (_, t) => t)
                .FirstOrDefaultAsync(ct);
        }

        tenant ??= await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Kind == TenantKind.Platform && t.DeletedAt == null, ct);
        if (tenant?.TermsUrl is { } terms)
        {
            view["TermsUrl"] = terms;
        }

        if (tenant?.PrivacyUrl is { } privacy)
        {
            view["PrivacyUrl"] = privacy;
        }
    }
}

public static class IdentityRateLimits
{
    public const string Login = "login";
    public const string ForgotPassword = "forgot-password";
}
