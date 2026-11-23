using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Directory;

public enum PostingDecision { Allow, Moderate, Refuse }

/// <summary>
/// Who may send to a group (PROMPT 7.3): anyone, the organisation, members, or managers; outsiders only when the group
/// allows external senders; in a moderated group everyone but managers waits for a moderator.
/// </summary>
public sealed class GroupPosting(BlueMailDbContext db)
{
    public async Task<PostingDecision> CheckAsync(Guid tenant, Guid groupId, string? sender, CancellationToken ct)
    {
        var g = await db.Groups.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId && x.TenantId == tenant, ct);
        if (g is null)
        {
            return PostingDecision.Refuse;
        }

        var s = sender?.ToLowerInvariant();
        var domain = s is not null && s.Contains('@', StringComparison.Ordinal) ? s[(s.LastIndexOf('@') + 1)..] : null;
        var internalSender = domain is not null && await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant && d.Domain == domain && d.DeletedAt == null, ct);
        var senderUser = s is null ? null
            : await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == tenant && u.PrimaryEmail == s && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
              ?? await db.UserAliases.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == tenant && a.Email == s && a.DeletedAt == null).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct);
        var member = s is null ? null : await db.GroupMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.GroupId == groupId && m.DeletedAt == null && ((senderUser != null && m.UserId == senderUser) || m.ExternalEmail == s)).FirstOrDefaultAsync(ct);
        var manager = member is { Role: GroupMemberRole.Owner or GroupMemberRole.Manager };

        if (!internalSender && !g.AllowExternal && member is null)
        {
            return PostingDecision.Refuse;
        }

        var allowed = g.WhoCanPost switch
        {
            GroupPostPermission.Anyone => true,
            GroupPostPermission.Domain => internalSender || g.AllowExternal,
            GroupPostPermission.Members => member is not null,
            GroupPostPermission.Managers => manager,
            _ => false,
        };
        if (!allowed)
        {
            return PostingDecision.Refuse;
        }

        return g.Moderated && !manager ? PostingDecision.Moderate : PostingDecision.Allow;
    }
}
