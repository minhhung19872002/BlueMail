using BlueMail.Domain.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>
/// A change applied to a set of messages. Null flag = unchanged. Starred and Important are flags mirrored by the
/// STARRED / IMPORTANT system labels, so IMAP clients see them as folders (PROMPT phase 11).
/// </summary>
public sealed record MessageChange
{
    public bool? Seen { get; init; }
    public bool? Starred { get; init; }
    public bool? Important { get; init; }
    public IReadOnlyList<string> AddSystem { get; init; } = [];
    public IReadOnlyList<string> RemoveSystem { get; init; } = [];
    public IReadOnlyList<Guid> AddLabels { get; init; } = [];
    public IReadOnlyList<Guid> RemoveLabels { get; init; } = [];

    /// <summary>Delete forever: only for messages in Trash or Spam (the caller checks); removes them from every label.</summary>
    public bool Expunge { get; init; }

    // IMAP flags (RFC 3501 2.3.2): \Answered, $Forwarded, \Draft, keywords; \Deleted is per folder (label).
    public bool? Answered { get; init; }
    public bool? Forwarded { get; init; }
    public bool? Draft { get; init; }
    public IReadOnlyList<string> AddKeywords { get; init; } = [];
    public IReadOnlyList<string> RemoveKeywords { get; init; } = [];
    public (Guid LabelId, bool Value)? DeletedIn { get; init; }
}

public sealed record ChangeResult(int Messages, long Modseq);

public sealed class LabelNotFoundException() : Exception("Label not found in this mailbox.");

public sealed class LabelConflictException() : Exception("A label with this name already exists here.");

public sealed partial class PgMailStore
{
    internal const string ExpungeSql = """
        UPDATE mail.messages SET deleted_at = coalesce(deleted_at, now()), modseq = @modseq, updated_at = now(),
            expunged_at = CASE WHEN legal_hold_count = 0
                                    AND received_at < now() - make_interval(days => compliance.f_keep_min_days(tenant_id, mailbox_id))
                               THEN now() END
        WHERE tenant_id = @t AND id = ANY(@ids) AND expunged_at IS NULL
        """;

    /// <summary>Messages of the given threads and/or ids that belong to the mailbox (others are silently dropped).</summary>
    public async Task<IReadOnlyList<Guid>> ResolveMessagesAsync(MailboxAccess access, IReadOnlyCollection<Guid> threadIds, IReadOnlyCollection<Guid> messageIds,
        CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var threads = threadIds.ToList();
        var ids = messageIds.ToList();
        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.DeletedAt == null && m.ExpungedAt == null
                        && (threads.Contains(m.ThreadId) || ids.Contains(m.Id)))
            .Select(m => m.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Applies a change atomically: one modseq for the whole operation, new IMAP UIDs for added labels, and a
    /// label_changes row per (label, UID) touched, so web sync and IMAP CONDSTORE/QRESYNC see the same history.
    /// </summary>
    public async Task<ChangeResult> ApplyAsync(MailboxAccess access, IReadOnlyCollection<Guid> messageIds, MessageChange change, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new ChangeResult(0, 0);
        }

        var add = new HashSet<Guid>(change.AddLabels);
        var remove = new HashSet<Guid>(change.RemoveLabels);
        var addKeys = change.AddSystem.ToList();
        var removeKeys = change.RemoveSystem.ToList();
        if (change.Starred is { } s)
        {
            (s ? addKeys : removeKeys).Add(SystemLabels.Starred);
        }

        if (change.Important is { } imp)
        {
            (imp ? addKeys : removeKeys).Add(SystemLabels.Important);
        }

        long modseq;
        int touched;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var c = (NpgsqlConnection)db.Database.GetDbConnection();
            var t = (NpgsqlTransaction)tx.GetDbTransaction();
            modseq = await Scalar<long>(c, t, ct, "UPDATE mail.mailboxes SET highest_modseq = highest_modseq + 1 WHERE id = @mb AND tenant_id = @t RETURNING highest_modseq",
                ("mb", access.MailboxId), ("t", access.TenantId));

            // Label ids of this mailbox only: a label id of another mailbox is "not found", never applied.
            var labelRows = new List<(Guid Id, string? Key)>();
            await using (var cmd = new NpgsqlCommand("SELECT id, system_key FROM mail.labels WHERE tenant_id = @t AND mailbox_id = @mb AND deleted_at IS NULL", c, t))
            {
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("mb", access.MailboxId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    labelRows.Add((rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
                }
            }

            var known = labelRows.Select(l => l.Id).ToHashSet();
            if (!add.Concat(remove).All(known.Contains))
            {
                throw new LabelNotFoundException();
            }

            add.UnionWith(labelRows.Where(l => l.Key is not null && addKeys.Contains(l.Key)).Select(l => l.Id));
            remove.UnionWith(labelRows.Where(l => l.Key is not null && removeKeys.Contains(l.Key)).Select(l => l.Id));
            remove.ExceptWith(add); // a label both added and removed (move onto itself) stays

            // Lock the rows (concurrent changes to the same messages are serialised; the mailbox row orders modseqs).
            var live = new List<Guid>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT id FROM mail.messages WHERE tenant_id = @t AND mailbox_id = @mb AND id = ANY(@ids) AND deleted_at IS NULL AND expunged_at IS NULL
                ORDER BY id FOR UPDATE
                """, c, t))
            {
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("mb", access.MailboxId);
                cmd.Parameters.AddWithValue("ids", ids);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    live.Add(rd.GetGuid(0));
                }
            }

            var target = live.ToArray();
            var changedIds = new HashSet<Guid>();

            if (change.Expunge)
            {
                remove = known.ToHashSet();
                add.Clear();
            }

            if (!change.Expunge && (change.Seen is not null || change.Starred is not null || change.Important is not null))
            {
                await using var cmd = new NpgsqlCommand("""
                    UPDATE mail.messages SET seen = coalesce(@seen, seen), flagged = coalesce(@flag, flagged), is_important = coalesce(@imp, is_important),
                        modseq = @modseq, updated_at = now()
                    WHERE tenant_id = @t AND id = ANY(@ids)
                      AND (seen IS DISTINCT FROM coalesce(@seen, seen) OR flagged IS DISTINCT FROM coalesce(@flag, flagged) OR is_important IS DISTINCT FROM coalesce(@imp, is_important))
                    RETURNING id
                    """, c, t);
                cmd.Parameters.Add(new NpgsqlParameter("seen", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Seen ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("flag", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Starred ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("imp", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Important ?? DBNull.Value });
                cmd.Parameters.AddWithValue("modseq", modseq);
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("ids", target);
                var flagged = new List<Guid>();
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await rd.ReadAsync(ct))
                    {
                        flagged.Add(rd.GetGuid(0));
                    }
                }

                if (flagged.Count > 0)
                {
                    await Exec(c, t, ct, """
                        INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at)
                        SELECT tenant_id, label_id, imap_uid, @modseq, 'FLAGS', now() FROM mail.message_labels WHERE tenant_id = @t AND message_id = ANY(@ids)
                        """, ("modseq", modseq), ("t", access.TenantId), ("ids", flagged.ToArray()));
                    await Exec(c, t, ct, "UPDATE mail.message_labels SET modseq = @modseq WHERE tenant_id = @t AND message_id = ANY(@ids)",
                        ("modseq", modseq), ("t", access.TenantId), ("ids", flagged.ToArray()));
                    changedIds.UnionWith(flagged);
                }
            }

            if (!change.Expunge && (change.Answered is not null || change.Forwarded is not null || change.Draft is not null
                                    || change.AddKeywords.Count > 0 || change.RemoveKeywords.Count > 0))
            {
                await using var cmd = new NpgsqlCommand("""
                    WITH upd AS (
                        UPDATE mail.messages m SET answered = coalesce(@ans, answered), forwarded = coalesce(@fwd, forwarded), draft = coalesce(@dr, draft),
                            keywords = ARRAY(SELECT DISTINCT k FROM unnest(array_cat(m.keywords, @add)) k WHERE NOT k = ANY(@rem) ORDER BY k),
                            modseq = @modseq, updated_at = now()
                        WHERE tenant_id = @t AND id = ANY(@ids)
                          AND (answered IS DISTINCT FROM coalesce(@ans, answered) OR forwarded IS DISTINCT FROM coalesce(@fwd, forwarded)
                               OR draft IS DISTINCT FROM coalesce(@dr, draft)
                               OR keywords IS DISTINCT FROM ARRAY(SELECT DISTINCT k FROM unnest(array_cat(m.keywords, @add)) k WHERE NOT k = ANY(@rem) ORDER BY k))
                        RETURNING id)
                    SELECT id FROM upd
                    """, c, t);
                cmd.Parameters.Add(new NpgsqlParameter("ans", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Answered ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("fwd", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Forwarded ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("dr", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.Draft ?? DBNull.Value });
                cmd.Parameters.AddWithValue("add", change.AddKeywords.ToArray());
                cmd.Parameters.AddWithValue("rem", change.RemoveKeywords.ToArray());
                cmd.Parameters.AddWithValue("modseq", modseq);
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("ids", target);
                var flagged = new List<Guid>();
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await rd.ReadAsync(ct))
                    {
                        flagged.Add(rd.GetGuid(0));
                    }
                }

                if (flagged.Count > 0)
                {
                    await Exec(c, t, ct, """
                        INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at)
                        SELECT tenant_id, label_id, imap_uid, @modseq, 'FLAGS', now() FROM mail.message_labels WHERE tenant_id = @t AND message_id = ANY(@ids)
                        """, ("modseq", modseq), ("t", access.TenantId), ("ids", flagged.ToArray()));
                    await Exec(c, t, ct, "UPDATE mail.message_labels SET modseq = @modseq WHERE tenant_id = @t AND message_id = ANY(@ids)",
                        ("modseq", modseq), ("t", access.TenantId), ("ids", flagged.ToArray()));
                    changedIds.UnionWith(flagged);
                }
            }

            if (!change.Expunge && change.DeletedIn is { } del)
            {
                await using var cmd = new NpgsqlCommand("""
                    WITH upd AS (
                        UPDATE mail.message_labels SET imap_deleted = @v, modseq = @modseq
                        WHERE tenant_id = @t AND label_id = @l AND message_id = ANY(@ids) AND imap_deleted <> @v
                        RETURNING message_id, imap_uid)
                    , logged AS (
                        INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at)
                        SELECT @t, @l, imap_uid, @modseq, 'FLAGS', now() FROM upd)
                    SELECT message_id FROM upd
                    """, c, t);
                cmd.Parameters.AddWithValue("v", del.Value);
                cmd.Parameters.AddWithValue("modseq", modseq);
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("l", del.LabelId);
                cmd.Parameters.AddWithValue("ids", target);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    changedIds.Add(rd.GetGuid(0));
                }
            }

            if (remove.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    WITH gone AS (
                        DELETE FROM mail.message_labels WHERE tenant_id = @t AND message_id = ANY(@ids) AND label_id = ANY(@labels)
                        RETURNING message_id, label_id, imap_uid)
                    , logged AS (
                        INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at)
                        SELECT @t, label_id, imap_uid, @modseq, 'REMOVED', now() FROM gone)
                    SELECT DISTINCT message_id FROM gone
                    """, c, t);
                cmd.Parameters.AddWithValue("t", access.TenantId);
                cmd.Parameters.AddWithValue("ids", target);
                cmd.Parameters.AddWithValue("labels", remove.ToArray());
                cmd.Parameters.AddWithValue("modseq", modseq);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    changedIds.Add(rd.GetGuid(0));
                }
            }

            foreach (var label in add)
            {
                // Messages not yet in the label get consecutive new UIDs (RFC 3501: UIDs strictly ascending per folder).
                var missing = new List<Guid>();
                // New UIDs follow the caller's order (IMAP COPY keeps the source order, RFC 3501 6.4.7).
                var inOrder = ids.Where(id => Array.IndexOf(target, id) >= 0).ToArray();
                await using (var cmd = new NpgsqlCommand("""
                    SELECT u.m FROM unnest(@ids) WITH ORDINALITY AS u(m, ord)
                    WHERE NOT EXISTS (SELECT 1 FROM mail.message_labels ml WHERE ml.tenant_id = @t AND ml.message_id = u.m AND ml.label_id = @l)
                    ORDER BY u.ord
                    """, c, t))
                {
                    cmd.Parameters.AddWithValue("ids", inOrder);
                    cmd.Parameters.AddWithValue("t", access.TenantId);
                    cmd.Parameters.AddWithValue("l", label);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        missing.Add(rd.GetGuid(0));
                    }
                }

                if (missing.Count == 0)
                {
                    continue;
                }

                var first = await Scalar<long>(c, t, ct, "UPDATE mail.labels SET uid_next = uid_next + @n WHERE id = @l RETURNING uid_next - @n",
                    ("n", (long)missing.Count), ("l", label));
                await Exec(c, t, ct, """
                    WITH added AS (
                        INSERT INTO mail.message_labels (tenant_id, message_id, label_id, imap_uid, modseq)
                        SELECT @t, m.id, @l, @first + m.ord - 1, @modseq FROM unnest(@ids) WITH ORDINALITY AS m(id, ord)
                        RETURNING imap_uid)
                    INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at)
                    SELECT @t, @l, imap_uid, @modseq, 'ADDED', now() FROM added
                    """, ("t", access.TenantId), ("l", label), ("first", first), ("modseq", modseq), ("ids", missing.ToArray()));
                changedIds.UnionWith(missing);
            }

            if (change.Expunge)
            {
                // Gone for the user in every case; really removed only when no legal hold covers it and KEEP_MIN retention
                // has passed - decided in this same statement (PROMPT 7.5). Hidden copies stay for compliance search.
                await Exec(c, t, ct, ExpungeSql, ("modseq", modseq), ("t", access.TenantId), ("ids", target));
                changedIds.UnionWith(target);
            }

            if (changedIds.Count > 0)
            {
                await Exec(c, t, ct, "UPDATE mail.messages SET modseq = @modseq, updated_at = now() WHERE tenant_id = @t AND id = ANY(@ids)",
                    ("modseq", modseq), ("t", access.TenantId), ("ids", changedIds.ToArray()));
            }

            touched = changedIds.Count;
            await tx.CommitAsync(ct);
        }

        if (touched > 0)
        {
            await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "message.changed");
        }

        return new ChangeResult(touched, modseq);
    }

    public async Task SetThreadMutedAsync(MailboxAccess access, IReadOnlyCollection<Guid> threadIds, bool muted, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        var ids = threadIds.ToList();
        var n = await db.Threads.IgnoreQueryFilters()
            .Where(t => t.TenantId == access.TenantId && t.MailboxId == access.MailboxId && ids.Contains(t.Id))
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.Muted, muted).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (n > 0)
        {
            var modseq = await NextModseqAsync(access, ct);
            await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "thread.changed");
        }
    }

    /// <summary>A new UIDVALIDITY for the mailbox: the clock, but always above every value used before (deleted labels too).</summary>
    private async Task<long> NextUidValidityAsync(MailboxAccess access, CancellationToken ct)
    {
        var used = await db.Labels.IgnoreQueryFilters().Where(l => l.MailboxId == access.MailboxId && l.TenantId == access.TenantId)
            .MaxAsync(l => (long?)l.UidValidity, ct) ?? 0;
        return Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), used + 1);
    }

    /// <summary>Creates a user label (nested with "/" semantics through <paramref name="parentId"/>).</summary>
    public async Task<Guid> CreateLabelAsync(MailboxAccess access, string name, Guid? parentId, string? colorBg, string? colorText, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        if (parentId is { } p && !await db.Labels.IgnoreQueryFilters().AnyAsync(l => l.Id == p && l.MailboxId == access.MailboxId && l.TenantId == access.TenantId && l.DeletedAt == null, ct))
        {
            throw new LabelNotFoundException();
        }

        var label = new Label
        {
            TenantId = access.TenantId, MailboxId = access.MailboxId, Name = name.Trim(), ParentId = parentId, ColorBg = colorBg, ColorText = colorText,
            UidValidity = await NextUidValidityAsync(access, ct), UidNext = 1,
        };
        db.Labels.Add(label);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw new LabelConflictException();
        }

        var modseq = await NextModseqAsync(access, ct);
        await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "label.changed");
        return label.Id;
    }

    public async Task UpdateLabelAsync(MailboxAccess access, Guid labelId, string name, string? colorBg, string? colorText,
        LabelVisibility? showInList = null, bool? showInMessageList = null, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        var label = await db.Labels.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == labelId && l.MailboxId == access.MailboxId && l.TenantId == access.TenantId && l.DeletedAt == null && l.SystemKey == null, ct)
            ?? throw new LabelNotFoundException();
        label.Name = name.Trim();
        label.ColorBg = colorBg;
        label.ColorText = colorText;
        label.ShowInList = showInList ?? label.ShowInList;
        label.ShowInMessageList = showInMessageList ?? label.ShowInMessageList;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw new LabelConflictException();
        }

        var modseq = await NextModseqAsync(access, ct);
        await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "label.changed");
    }

    /// <summary>Deletes a user label and its sub-labels; messages stay (they only lose the label).</summary>
    public async Task DeleteLabelAsync(MailboxAccess access, Guid labelId, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        var all = await db.Labels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.MailboxId == access.MailboxId && l.TenantId == access.TenantId && l.DeletedAt == null)
            .Select(l => new { l.Id, l.ParentId, l.SystemKey }).ToListAsync(ct);
        var root = all.FirstOrDefault(l => l.Id == labelId && l.SystemKey == null) ?? throw new LabelNotFoundException();
        var doomed = new HashSet<Guid> { root.Id };
        bool grew;
        do
        {
            grew = false;
            foreach (var l in all.Where(l => l.ParentId is { } p && doomed.Contains(p) && !doomed.Contains(l.Id)))
            {
                doomed.Add(l.Id);
                grew = true;
            }
        }
        while (grew);

        var members = await db.MessageLabels.IgnoreQueryFilters().AsNoTracking()
            .Where(ml => ml.TenantId == access.TenantId && doomed.Contains(ml.LabelId)).Select(ml => ml.MessageId).Distinct().ToListAsync(ct);
        if (members.Count > 0)
        {
            await ApplyAsync(access, members, new MessageChange { RemoveLabels = doomed.ToList() }, ct);
        }

        var now = DateTimeOffset.UtcNow;
        await db.Labels.IgnoreQueryFilters().Where(l => doomed.Contains(l.Id))
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.DeletedAt, now), ct);
        var modseq = await NextModseqAsync(access, ct);
        await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "label.changed");
    }

    public Task<bool> IsImageSenderTrustedAsync(MailboxAccess access, string address, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var a = address.ToLowerInvariant();
        return db.TrustedImageSenders.IgnoreQueryFilters().AnyAsync(x => x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.Address == a && x.DeletedAt == null, ct);
    }

    public async Task TrustImageSenderAsync(MailboxAccess access, string address, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        if (await IsImageSenderTrustedAsync(access, address, ct))
        {
            return;
        }

        db.TrustedImageSenders.Add(new TrustedImageSender { TenantId = access.TenantId, MailboxId = access.MailboxId, Address = address.ToLowerInvariant() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear(); // a parallel request trusted the same sender
        }
    }

    public async Task<IReadOnlyList<Guid>> GetMessagesThreadsAsync(MailboxAccess access, IReadOnlyCollection<Guid> messageIds, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var ids = messageIds.ToList();
        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && ids.Contains(m.Id))
            .Select(m => m.ThreadId).Distinct().ToListAsync(ct);
    }

    /// <summary>True when every given message is in Trash or Spam (the precondition of delete forever).</summary>
    public async Task<bool> InTrashOrSpamAsync(MailboxAccess access, IReadOnlyCollection<Guid> messageIds, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        if (messageIds.Count == 0)
        {
            return false;
        }

        var ids = messageIds.ToList();
        var bins = await db.Labels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == access.TenantId && l.MailboxId == access.MailboxId && (l.SystemKey == SystemLabels.Trash || l.SystemKey == SystemLabels.Spam))
            .Select(l => l.Id).ToListAsync(ct);
        var inBin = await db.MessageLabels.IgnoreQueryFilters().AsNoTracking()
            .Where(ml => ml.TenantId == access.TenantId && ids.Contains(ml.MessageId) && bins.Contains(ml.LabelId))
            .Select(ml => ml.MessageId).Distinct().CountAsync(ct);
        return inBin == ids.Distinct().Count();
    }

    /// <summary>
    /// Sets \Answered or $Forwarded after a reply/forward was sent: a flag change like any other, with a new modseq
    /// and FLAGS rows in label_changes so IMAP clients see it (RFC 7162).
    /// </summary>
    public async Task MarkRepliedAsync(MailboxAccess access, Guid messageId, bool forwarded, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        long modseq;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var c = (NpgsqlConnection)db.Database.GetDbConnection();
            var t = (NpgsqlTransaction)tx.GetDbTransaction();
            modseq = await Scalar<long>(c, t, ct, "UPDATE mail.mailboxes SET highest_modseq = highest_modseq + 1 WHERE id = @mb AND tenant_id = @t RETURNING highest_modseq",
                ("mb", access.MailboxId), ("t", access.TenantId));
            var column = forwarded ? "forwarded" : "answered";
            var n = await Scalar<long>(c, t, ct,
                $"WITH u AS (UPDATE mail.messages SET {column} = true, modseq = @modseq, updated_at = now() " +
                $"WHERE tenant_id = @t AND mailbox_id = @mb AND id = @m AND NOT {column} RETURNING id) SELECT count(*) FROM u",
                ("modseq", modseq), ("t", access.TenantId), ("mb", access.MailboxId), ("m", messageId));
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            await Exec(c, t, ct,
                "INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at) " +
                "SELECT tenant_id, label_id, imap_uid, @modseq, 'FLAGS', now() FROM mail.message_labels WHERE tenant_id = @t AND message_id = @m",
                ("modseq", modseq), ("t", access.TenantId), ("m", messageId));
            await Exec(c, t, ct, "UPDATE mail.message_labels SET modseq = @modseq WHERE tenant_id = @t AND message_id = @m",
                ("modseq", modseq), ("t", access.TenantId), ("m", messageId));
            await tx.CommitAsync(ct);
        }

        await events.PublishAsync(access.TenantId, access.MailboxId, modseq, "message.changed");
    }

    /// <summary>
    /// Claims one due send for submission (UNDO_WINDOW/SCHEDULED past send_at, or SUBMITTING whose lease expired):
    /// one conditional UPDATE with SKIP LOCKED, so two dispatchers never take the same send.
    /// </summary>
    public async Task<Guid?> ClaimDueSendAsync(TimeSpan lease, CancellationToken ct = default)
    {
        var seconds = lease.TotalSeconds;
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE mail.outgoing SET status = 'SUBMITTING', claimed_at = now(), updated_at = now()
            WHERE id = (
                SELECT id FROM mail.outgoing
                WHERE deleted_at IS NULL AND (
                      (status IN ('UNDO_WINDOW', 'SCHEDULED') AND send_at <= now())
                   OR (status = 'SUBMITTING' AND claimed_at < now() - make_interval(secs => {seconds})))
                ORDER BY send_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        return claimed.Count == 0 ? null : claimed[0];
    }
}
