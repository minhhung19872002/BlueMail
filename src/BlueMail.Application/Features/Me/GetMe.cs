using BlueMail.Application.Common;
using BlueMail.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Me;

public sealed record GetMeQuery : IRequest<MeDto>;

public sealed record MeDto(
    Guid Id,
    string Email,
    string DisplayName,
    Guid TenantId,
    string TenantName,
    string Locale,
    string TimeZone,
    string? HourCycle,
    int? WeekStart,
    bool IsPlatformAdmin,
    bool IsTenantAdmin,
    bool TwoFactorEnabled,
    bool MustChangePassword,
    IReadOnlyCollection<string> Permissions,
    string Branding,
    IReadOnlyList<string> Domains,
    bool ExternalSenderBanner = true,
    string? SupportActor = null);

public sealed class GetMeHandler(BlueMailDbContext db, ICurrentUser me, Admin.Policies.PolicyService policies) : IRequestHandler<GetMeQuery, MeDto>
{
    public async Task<MeDto> Handle(GetMeQuery request, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == me.UserId)
            .Select(u => new
            {
                u.Id, u.PrimaryEmail, u.DisplayName, u.TenantId, u.Locale, u.TimeZone, u.HourCycle, u.WeekStart,
                u.IsPlatformAdmin, u.IsTenantAdmin, u.TwoFactorEnabled, u.MustChangePassword,
                Tenant = db.Tenants.Where(t => t.Id == u.TenantId).Select(t => new { t.Name, t.DefaultLocale, t.DefaultTimeZone, t.Branding }).First(),
            })
            .FirstOrDefaultAsync(ct) ?? throw AppException.NotFound();

        // The tenant's own domains: the web tells "external sender" and look-alike domains from them (PROMPT 6.3).
        var domains = await db.Domains.AsNoTracking().Where(d => d.TenantId == row.TenantId).OrderBy(d => d.Domain).Select(d => d.Domain).ToListAsync(ct);

        // Locale/time zone: user setting -> tenant default (PROMPT 0.2).
        return new MeDto(row.Id, row.PrimaryEmail, row.DisplayName, row.TenantId, row.Tenant.Name,
            row.Locale ?? row.Tenant.DefaultLocale, row.TimeZone ?? row.Tenant.DefaultTimeZone, row.HourCycle, row.WeekStart,
            row.IsPlatformAdmin, row.IsTenantAdmin, row.TwoFactorEnabled, row.MustChangePassword,
            me.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(), row.Tenant.Branding, domains,
            await policies.BoolAsync(row.TenantId, await policies.OrgUnitOfAsync(row.Id, ct), Admin.Policies.PolicyKeys.ExternalSenderBanner, ct),
            // A support sign-in (PROMPT 7.1) is always visible to whoever is looking at the screen.
            await db.Sessions.IgnoreQueryFilters().Where(s => s.Id == me.SessionId && s.ActorUserId != null)
                .Select(s => db.Users.IgnoreQueryFilters().Where(u => u.Id == s.ActorUserId).Select(u => u.PrimaryEmail).FirstOrDefault()).FirstOrDefaultAsync(ct));
    }
}
