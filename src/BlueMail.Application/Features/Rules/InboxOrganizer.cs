using System.Text.Json;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Rules;

/// <summary>Why a message got its tab and importance: shown to the user ("explainable", PROMPT 6.5, 6.6).</summary>
public sealed record Organization(string Category, string CategoryReason, bool Important, string? ImportantReason, bool MutedThread, bool Personal);

/// <summary>
/// Inbox tabs and automatic importance at delivery, with explainable rules only (PROMPT 6.5, 6.6):
/// contacts → Primary; administrator/system domain rules; mailing-list and bulk headers; automatic mail → Updates.
/// Important: people you write to often, or a colleague writing directly to you. Muted conversations stay out of
/// the Inbox unless the message is addressed to you personally.
/// </summary>
public sealed class InboxOrganizer(BlueMailDbContext db)
{
    public const int FrequentContact = 3;

    private sealed record Match(string? FromDomain, string? ListId);

    public async Task<Organization> OrganizeAsync(Guid tenant, Guid mailboxId, Guid? userId, IReadOnlyCollection<string> myAddresses, MessageFacts f, CancellationToken ct)
    {
        var from = f.From?.Address.ToLowerInvariant();
        var domain = from is not null && from.Contains('@', StringComparison.Ordinal) ? from[(from.LastIndexOf('@') + 1)..] : null;
        var personal = f.To.Concat(f.Cc).Any(a => myAddresses.Contains(a.Address.ToLowerInvariant()));
        var contactCount = -1;
        if (userId is { } uid && from is not null)
        {
            contactCount = await db.Contacts.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenant && c.UserId == uid && c.DeletedAt == null && c.EmailKeys.Contains(from))
                .Select(c => (int?)c.ContactCount).MaxAsync(ct) ?? -1;
        }

        var (category, reason) = await CategoryAsync(tenant, f, from, domain, contactCount >= 0, ct);
        var bulk = f.Bulk || f.ListId is not null || f.ListUnsubscribe is not null || f.AutoSubmitted;
        string? importantReason = null;
        if (!bulk && contactCount >= FrequentContact)
        {
            importantReason = "frequent_contact";
        }
        else if (!bulk && personal && f.To.Any(a => myAddresses.Contains(a.Address.ToLowerInvariant())) && domain is not null
                 && await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant && d.Domain == domain && d.DeletedAt == null, ct))
        {
            importantReason = "colleague_direct";
        }

        var muted = await MutedThreadAsync(tenant, mailboxId, f, ct);
        return new Organization(category, reason, importantReason is not null, importantReason, muted, personal);
    }

    private async Task<(string Category, string Reason)> CategoryAsync(Guid tenant, MessageFacts f, string? from, string? domain, bool isContact, CancellationToken ct)
    {
        if (isContact)
        {
            return (SystemLabels.CategoryPrimary, "contact");
        }

        var rules = await db.CategoryRules.AsNoTracking().Where(r => r.IsEnabled && (r.TenantId == null || r.TenantId == tenant))
            .OrderBy(r => r.TenantId == null).ThenBy(r => r.SortOrder).ToListAsync(ct); // the tenant's own rules first
        foreach (var r in rules)
        {
            var m = JsonSerializer.Deserialize<Match>(r.Match, FilterActions.Json);
            if (m is null)
            {
                continue;
            }

            var ok = (m.FromDomain is null || (domain is not null && (domain == m.FromDomain || domain.EndsWith("." + m.FromDomain, StringComparison.Ordinal))))
                     && (m.ListId is null || (f.ListId?.Contains(m.ListId, StringComparison.OrdinalIgnoreCase) ?? false))
                     && (m.FromDomain is not null || m.ListId is not null);
            if (ok)
            {
                return (r.Category, "rule:" + r.Id);
            }
        }

        var local = from is null ? "" : from[..Math.Max(0, from.IndexOf('@'))];
        if (f.ListId is not null && f.ListUnsubscribe is null)
        {
            return (SystemLabels.CategoryForums, "list_id");
        }

        if (f.ListUnsubscribe is not null || f.Bulk)
        {
            return (SystemLabels.CategoryPromotions, "bulk");
        }

        if (f.AutoSubmitted || local is "noreply" or "no-reply" or "notifications" or "notification" or "alerts" or "do-not-reply" or "donotreply")
        {
            return (SystemLabels.CategoryUpdates, "automatic");
        }

        return (SystemLabels.CategoryPrimary, "default");
    }

    /// <summary>The conversation a reply belongs to is muted (by In-Reply-To / References in this mailbox).</summary>
    private async Task<bool> MutedThreadAsync(Guid tenant, Guid mailboxId, MessageFacts f, CancellationToken ct)
    {
        var refs = f.References.Concat(f.InReplyTo is null ? [] : [f.InReplyTo]).Select(r => r.Trim('<', '>')).Distinct().Take(50).ToList();
        if (refs.Count == 0)
        {
            return false;
        }

        return await (from msg in db.Messages.IgnoreQueryFilters()
                      join t in db.Threads.IgnoreQueryFilters() on msg.ThreadId equals t.Id
                      where msg.TenantId == tenant && msg.MailboxId == mailboxId && msg.RfcMessageId != null && refs.Contains(msg.RfcMessageId) && t.Muted
                      select msg.Id).AnyAsync(ct);
    }
}
