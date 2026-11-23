using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>PostgreSQL implementation of <see cref="IMailStore"/>.</summary>
public sealed partial class PgMailStore(BlueMailDbContext db, BlobStore blobs, IMailEvents events) : IMailStore
{
    public async Task<Guid> ProvisionMailboxAsync(Guid tenantId, Guid ownerId, MailboxOwnerKind kind, CancellationToken ct = default)
    {
        var existing = await db.Mailboxes.IgnoreQueryFilters()
            .Where(m => m.OwnerId == ownerId && m.TenantId == tenantId && m.DeletedAt == null)
            .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            return id;
        }

        var mailbox = new Mailbox { Id = Guid.NewGuid(), TenantId = tenantId, OwnerId = ownerId, OwnerKind = kind };
        db.Mailboxes.Add(mailbox);
        // UIDVALIDITY: seconds since epoch at creation, distinct per label creation instant (RFC 3501 2.3.1.1).
        var uidValidity = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var key in SystemLabels.All)
        {
            db.Labels.Add(new Label
            {
                TenantId = tenantId,
                MailboxId = mailbox.Id,
                SystemKey = key,
                Name = key,
                UidValidity = uidValidity,
                UidNext = 1,
                ShowInList = key.StartsWith("CATEGORY_", StringComparison.Ordinal) ? LabelVisibility.Hide : LabelVisibility.Show,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return mailbox.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent provisioning won the unique index on owner_id; use its mailbox.
            db.ChangeTracker.Clear();
            return await db.Mailboxes.IgnoreQueryFilters()
                .Where(m => m.OwnerId == ownerId && m.TenantId == tenantId && m.DeletedAt == null)
                .Select(m => m.Id).FirstAsync(ct);
        }
    }

    public async Task<MailboxAccess> OpenAsync(Guid tenantId, Guid userId, Guid? mailboxId, CancellationToken ct = default)
    {
        // Every lookup is bound to the tenant explicitly, independent of the ambient tenant filter.
        if (mailboxId is null)
        {
            var own = await db.Mailboxes.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.OwnerId == userId && m.OwnerKind == MailboxOwnerKind.User && m.DeletedAt == null)
                .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct)
                ?? throw new MailboxAccessDeniedException();
            return new MailboxAccess(tenantId, own, userId, MailboxRights.Owner);
        }

        var target = await db.Mailboxes.IgnoreQueryFilters()
            .Where(m => m.Id == mailboxId && m.TenantId == tenantId && m.DeletedAt == null)
            .Select(m => new { m.Id, m.OwnerId, m.OwnerKind })
            .FirstOrDefaultAsync(ct) ?? throw new MailboxAccessDeniedException();

        if (target.OwnerKind == MailboxOwnerKind.User)
        {
            return target.OwnerId == userId
                ? new MailboxAccess(tenantId, target.Id, userId, MailboxRights.Owner)
                : throw new MailboxAccessDeniedException();
        }

        var grant = await db.SharedMailboxMembers.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.SharedMailboxId == target.OwnerId && g.UserId == userId && g.DeletedAt == null)
            .Select(g => new { g.CanRead, g.CanSendAs, g.CanSendOnBehalf })
            .FirstOrDefaultAsync(ct) ?? throw new MailboxAccessDeniedException();

        var rights = MailboxRights.None;
        if (grant.CanRead)
        {
            rights |= MailboxRights.Read | MailboxRights.Write;
        }

        if (grant.CanSendAs)
        {
            rights |= MailboxRights.SendAs;
        }

        if (grant.CanSendOnBehalf)
        {
            rights |= MailboxRights.SendOnBehalf;
        }

        return rights == MailboxRights.None ? throw new MailboxAccessDeniedException() : new MailboxAccess(tenantId, target.Id, userId, rights);
    }

    public async Task<IReadOnlyList<LabelSummary>> GetLabelsAsync(MailboxAccess access, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var labels = db.Labels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == access.TenantId && l.MailboxId == access.MailboxId && l.DeletedAt == null);
        var live = db.Messages.IgnoreQueryFilters()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.ExpungedAt == null && m.DeletedAt == null);

        // Counts come from the same rows as the lists (PROMPT 14.7), computed per request.
        return await labels
            .OrderBy(l => l.SystemKey == null).ThenBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LabelSummary(
                l.Id, l.SystemKey, l.Name, l.ParentId, l.ColorBg, l.ColorText, l.ShowInList, l.ShowInMessageList,
                db.MessageLabels.Count(ml => ml.TenantId == access.TenantId && ml.LabelId == l.Id && live.Any(m => m.Id == ml.MessageId)),
                db.MessageLabels.Count(ml => ml.TenantId == access.TenantId && ml.LabelId == l.Id && live.Any(m => m.Id == ml.MessageId && !m.Seen))))
            .ToListAsync(ct);
    }

    public async Task<long> GetUsedBytesAsync(MailboxAccess access, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        return await db.Messages.IgnoreQueryFilters()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.ExpungedAt == null && m.DeletedAt == null)
            .SumAsync(m => (long?)m.Size, ct) ?? 0;
    }

    /// <summary>
    /// Next modseq of a mailbox (RFC 7162), allocated atomically. Callers run it inside the transaction of the
    /// change it versions, so the row lock orders concurrent writers of one mailbox.
    /// </summary>
    internal async Task<long> NextModseqAsync(MailboxAccess access, CancellationToken ct)
    {
        access.Require(MailboxRights.Write);
        return (await db.Database.SqlQuery<long>(
            $"UPDATE mail.mailboxes SET highest_modseq = highest_modseq + 1 WHERE id = {access.MailboxId} AND tenant_id = {access.TenantId} RETURNING highest_modseq AS \"Value\"")
            .ToListAsync(ct)).Single();
    }

    /// <summary>Reserves <paramref name="count"/> consecutive IMAP UIDs in a label; returns the first.</summary>
    internal async Task<long> AllocateUidsAsync(MailboxAccess access, Guid labelId, int count, CancellationToken ct)
    {
        access.Require(MailboxRights.Write);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return (await db.Database.SqlQuery<long>(
            $"UPDATE mail.labels SET uid_next = uid_next + {count} WHERE id = {labelId} AND mailbox_id = {access.MailboxId} AND tenant_id = {access.TenantId} RETURNING uid_next - {count} AS \"Value\"")
            .ToListAsync(ct)).Single();
    }
}
