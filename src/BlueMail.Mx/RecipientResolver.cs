using BlueMail.Domain.Common;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Mx;

public enum RecipientStatus { Ok, NotOurDomain, Unknown, TenantSuspended, OverQuota }

/// <summary>Where mail for one RCPT TO goes: one or more mailboxes of one tenant.</summary>
public sealed record ResolvedRecipient(RecipientStatus Status, Guid TenantId, string Address, IReadOnlyList<Guid> MailboxIds, string? Kind = null);

/// <summary>
/// RCPT validation (PROMPT 2.1 step 3): only verified domains of an active tenant, and only addresses that exist
/// (user, alias, group, shared mailbox, catch-all). Unknown addresses are refused at RCPT - never accepted and
/// bounced later. Sub-addresses ("lan+news@") deliver to the base address. Lookups run before the tenant is known,
/// so each query binds the tenant of the domain explicitly.
/// </summary>
public sealed class RecipientResolver(BlueMailDbContext db)
{
    public const int MaxGroupExpansionDepth = 3;

    public async Task<ResolvedRecipient> ResolveAsync(string address, long declaredSize, CancellationToken ct)
    {
        if (!EmailAddress.TryParse(address, out var parsed))
        {
            return new ResolvedRecipient(RecipientStatus.Unknown, Guid.Empty, address, []);
        }

        var email = parsed.Value;
        var domain = await db.Domains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Domain == email.Domain && d.DeletedAt == null && d.VerifiedAt != null)
            .Select(d => new { d.TenantId, d.CatchAllTarget, Status = db.Tenants.IgnoreQueryFilters().Where(t => t.Id == d.TenantId).Select(t => t.Status).First() })
            .FirstOrDefaultAsync(ct);
        if (domain is null)
        {
            return new ResolvedRecipient(RecipientStatus.NotOurDomain, Guid.Empty, email.Value, []);
        }

        if (domain.Status == TenantStatus.Suspended)
        {
            return new ResolvedRecipient(RecipientStatus.TenantSuspended, domain.TenantId, email.Value, []);
        }

        var local = email.LocalPart;
        var plus = local.IndexOf('+', StringComparison.Ordinal);
        var candidates = plus > 0 ? new[] { email.Value, local[..plus] + "@" + email.Domain } : [email.Value];
        foreach (var candidate in candidates)
        {
            var r = await ResolveExactAsync(domain.TenantId, candidate, 0, ct);
            if (r is not null)
            {
                return await WithQuotaAsync(r, declaredSize, ct);
            }
        }

        if (domain.CatchAllTarget is { } catchAll && EmailAddress.TryParse(catchAll, out var target))
        {
            var r = await ResolveExactAsync(domain.TenantId, target.Value.Value, 0, ct);
            if (r is not null)
            {
                return await WithQuotaAsync(r with { Address = email.Value, Kind = "catch-all" }, declaredSize, ct);
            }
        }

        return new ResolvedRecipient(RecipientStatus.Unknown, domain.TenantId, email.Value, []);
    }

    private async Task<ResolvedRecipient?> ResolveExactAsync(Guid tenant, string email, int depth, CancellationToken ct)
    {
        var userId = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenant && u.PrimaryEmail == email && u.DeletedAt == null && u.Status != UserStatus.Deleted)
            .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
            ?? await db.UserAliases.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.TenantId == tenant && a.Email == email && a.DeletedAt == null)
                .Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct);
        if (userId is { } uid)
        {
            var mailbox = await MailboxOfAsync(tenant, uid, ct);
            return mailbox is null ? null : new ResolvedRecipient(RecipientStatus.Ok, tenant, email, [mailbox.Value], "user");
        }

        var sharedId = await db.SharedMailboxes.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenant && s.Email == email && s.DeletedAt == null).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (sharedId is { } sid)
        {
            var mailbox = await MailboxOfAsync(tenant, sid, ct);
            return mailbox is null ? null : new ResolvedRecipient(RecipientStatus.Ok, tenant, email, [mailbox.Value], "shared");
        }

        var groupId = await db.Groups.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.TenantId == tenant && g.Email == email && g.DeletedAt == null).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
        if (groupId is { } gid && depth < MaxGroupExpansionDepth)
        {
            // Group expansion at delivery (PROMPT 7.3): internal members' mailboxes. External members go through the
            // outbound queue (Phase 5); a group with only external members is still a valid recipient.
            var memberUsers = await db.GroupMembers.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenant && m.GroupId == gid && m.DeletedAt == null && m.UserId != null)
                .Select(m => m.UserId!.Value).ToListAsync(ct);
            var mailboxes = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenant && memberUsers.Contains(m.OwnerId) && m.DeletedAt == null)
                .Select(m => m.Id).ToListAsync(ct);
            return new ResolvedRecipient(RecipientStatus.Ok, tenant, email, mailboxes, "group");
        }

        return null;
    }

    private Task<Guid?> MailboxOfAsync(Guid tenant, Guid owner, CancellationToken ct) =>
        db.Mailboxes.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenant && m.OwnerId == owner && m.DeletedAt == null).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);

    /// <summary>Quota (PROMPT 2.5): usage recomputed from message sizes, compared with the owner's quota.</summary>
    private async Task<ResolvedRecipient> WithQuotaAsync(ResolvedRecipient r, long declaredSize, CancellationToken ct)
    {
        if (r.Kind != "user" || r.MailboxIds.Count != 1)
        {
            return r;
        }

        var mb = r.MailboxIds[0];
        var quota = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.Id == mb)
            .Select(m => db.Users.IgnoreQueryFilters().Where(u => u.Id == m.OwnerId).Select(u => u.QuotaBytes).FirstOrDefault()).FirstOrDefaultAsync(ct);
        if (quota <= 0)
        {
            return r;
        }

        var used = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == r.TenantId && m.MailboxId == mb && m.ExpungedAt == null && m.DeletedAt == null)
            .SumAsync(m => (long?)m.Size, ct) ?? 0;
        return used + Math.Max(0, declaredSize) > quota ? r with { Status = RecipientStatus.OverQuota } : r;
    }
}
