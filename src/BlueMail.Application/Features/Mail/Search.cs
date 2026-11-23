using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Mail;

/// <summary>Who is searching: the mailbox's own addresses ("me") and the user's time zone (dates are local days).</summary>
public sealed record SearchOwner(Guid TenantId, Guid MailboxId, IReadOnlyList<string> Addresses, TimeZoneInfo Zone);

public sealed class SearchContexts(BlueMailDbContext db)
{
    public async Task<SearchOwner> ForMailboxAsync(Guid tenantId, Guid mailboxId, Guid? actorUserId, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == mailboxId && m.TenantId == tenantId, ct);
        var addresses = new List<string>();
        string? tz = null;
        if (mailbox.OwnerKind == MailboxOwnerKind.User)
        {
            var u = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == mailbox.OwnerId).Select(x => new { x.PrimaryEmail, x.TimeZone }).FirstAsync(ct);
            addresses.Add(u.PrimaryEmail);
            tz = u.TimeZone;
            addresses.AddRange(await db.UserAliases.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.UserId == mailbox.OwnerId && a.DeletedAt == null).Select(a => a.Email).ToListAsync(ct));
        }
        else
        {
            addresses.AddRange(await db.SharedMailboxes.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == mailbox.OwnerId).Select(s => s.Email).ToListAsync(ct));
            if (actorUserId is { } actor)
            {
                tz = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == actor).Select(x => x.TimeZone).FirstOrDefaultAsync(ct);
            }
        }

        tz ??= await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct);
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var z) ? z : TimeZoneInfo.Utc;
        return new SearchOwner(tenantId, mailboxId, addresses.Select(a => a.ToLowerInvariant()).Distinct().ToList(), zone);
    }

    /// <summary>Parses a query for the API: a broken query is a 400 with a translated reason, never "search as words".</summary>
    public static SearchNode? Parse(string? q, SearchOwner owner, DateTimeOffset? now = null)
    {
        try
        {
            return SearchParser.Parse(q, new SearchContext(owner.Zone, now ?? DateTimeOffset.UtcNow));
        }
        catch (SearchSyntaxException e)
        {
            var code = "search_" + e.Code; // every code is translated: LocalizationTests checks SearchParser.ErrorCodes
            throw AppException.BadRequest(code, new Dictionary<string, object?> { ["position"] = e.Position, ["detail"] = e.Detail });
        }
    }
}
