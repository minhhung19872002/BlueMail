using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>A message as seen in one IMAP folder (label): its UID there, flags, size, date.</summary>
public sealed record ImapMessageRow(long Uid, Guid MessageId, long Modseq, bool Seen, bool Flagged, bool Answered, bool Forwarded, bool Draft, bool Deleted,
    string[] Keywords, DateTimeOffset ReceivedAt, long Size);

/// <summary>A change in a folder since a modseq (from mail.label_changes): ADDED, REMOVED or FLAGS of a UID.</summary>
public sealed record ImapChange(long Uid, string Change, long Modseq);

public sealed record ImapFolderStatus(long Messages, long Unseen, long UidNext, long UidValidity, long HighestModseq, long Size);

/// <summary>Reads for the IMAP server (Phase 11). Every write still goes through <see cref="ApplyAsync"/> / DeliverAsync.</summary>
public sealed partial class PgMailStore
{
    private const string RowColumns = """
        ml.imap_uid, m.id, greatest(m.modseq, ml.modseq), m.seen, m.flagged, m.answered, m.forwarded, m.draft, ml.imap_deleted, m.keywords, m.received_at, m.size
        """;

    private static ImapMessageRow ReadRow(NpgsqlDataReader rd) => new(rd.GetInt64(0), rd.GetGuid(1), rd.GetInt64(2), rd.GetBoolean(3), rd.GetBoolean(4),
        rd.GetBoolean(5), rd.GetBoolean(6), rd.GetBoolean(7), rd.GetBoolean(8), rd.GetFieldValue<string[]>(9), rd.GetFieldValue<DateTimeOffset>(10), rd.GetInt64(11));

    /// <summary>Every message of a folder, by UID: the state of a SELECT.</summary>
    public async Task<IReadOnlyList<ImapMessageRow>> ImapListAsync(MailboxAccess access, Guid labelId, CancellationToken ct = default) =>
        await ImapQueryAsync(access, labelId, null, ct);

    /// <summary>Current state of the given UIDs (after a change).</summary>
    public Task<IReadOnlyList<ImapMessageRow>> ImapRowsAsync(MailboxAccess access, Guid labelId, IReadOnlyCollection<long> uids, CancellationToken ct = default) =>
        ImapQueryAsync(access, labelId, uids, ct);

    private async Task<IReadOnlyList<ImapMessageRow>> ImapQueryAsync(MailboxAccess access, Guid labelId, IReadOnlyCollection<long>? uids, CancellationToken ct)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {RowColumns}
            FROM mail.message_labels ml JOIN mail.messages m ON m.tenant_id = ml.tenant_id AND m.id = ml.message_id
            JOIN mail.labels l ON l.id = ml.label_id AND l.mailbox_id = @mb
            WHERE ml.tenant_id = @t AND ml.label_id = @l AND m.deleted_at IS NULL AND m.expunged_at IS NULL AND (@all OR ml.imap_uid = ANY(@uids))
            ORDER BY ml.imap_uid
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("l", labelId);
        cmd.Parameters.AddWithValue("all", uids is null);
        cmd.Parameters.AddWithValue("uids", (uids ?? []).ToArray());
        var list = new List<ImapMessageRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(ReadRow(rd));
        }

        return list;
    }

    /// <summary>Changes of a folder after <paramref name="sinceModseq"/> (QRESYNC / CONDSTORE / IDLE notifications).</summary>
    public async Task<IReadOnlyList<ImapChange>> ImapChangesAsync(MailboxAccess access, Guid labelId, long sinceModseq, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT lc.imap_uid, lc.change, lc.modseq FROM mail.label_changes lc
            JOIN mail.labels l ON l.id = lc.label_id AND l.mailbox_id = @mb
            WHERE lc.tenant_id = @t AND lc.label_id = @l AND lc.modseq > @since
            ORDER BY lc.modseq, lc.imap_uid
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("l", labelId);
        cmd.Parameters.AddWithValue("since", sinceModseq);
        var list = new List<ImapChange>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new ImapChange(rd.GetInt64(0), rd.GetString(1), rd.GetInt64(2)));
        }

        return list;
    }

    public async Task<long> HighestModseqAsync(MailboxAccess access, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT highest_modseq FROM mail.mailboxes WHERE id = @mb AND tenant_id = @t", c);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>STATUS (RFC 3501 6.3.10): counts computed from the rows, never stored (PROMPT 2.5).</summary>
    public async Task<ImapFolderStatus> ImapStatusAsync(MailboxAccess access, Guid labelId, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT count(m.id), count(m.id) FILTER (WHERE NOT m.seen), l.uid_next, l.uid_validity, mb.highest_modseq, coalesce(sum(m.size), 0)
            FROM mail.labels l JOIN mail.mailboxes mb ON mb.id = l.mailbox_id
            LEFT JOIN mail.message_labels ml ON ml.tenant_id = l.tenant_id AND ml.label_id = l.id
            LEFT JOIN mail.messages m ON m.tenant_id = ml.tenant_id AND m.id = ml.message_id AND m.deleted_at IS NULL AND m.expunged_at IS NULL
            WHERE l.id = @l AND l.tenant_id = @t AND l.mailbox_id = @mb
            GROUP BY l.uid_next, l.uid_validity, mb.highest_modseq
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("l", labelId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct))
        {
            throw new LabelNotFoundException();
        }

        return new ImapFolderStatus(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5));
    }

    /// <summary>UIDs of messages in a folder (after COPY/MOVE/APPEND: COPYUID / APPENDUID).</summary>
    public async Task<IReadOnlyDictionary<Guid, long>> ImapUidsOfAsync(MailboxAccess access, Guid labelId, IReadOnlyCollection<Guid> messageIds, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT message_id, imap_uid FROM mail.message_labels WHERE tenant_id = @t AND label_id = @l AND message_id = ANY(@ids)", c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("l", labelId);
        cmd.Parameters.AddWithValue("ids", messageIds.ToArray());
        var map = new Dictionary<Guid, long>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            map[rd.GetGuid(0)] = rd.GetInt64(1);
        }

        return map;
    }

    /// <summary>The message with this Message-ID in a folder, if any (APPEND to Sent after SMTP submission: no duplicate).</summary>
    public async Task<Guid?> ImapFindByMessageIdAsync(MailboxAccess access, Guid labelId, string rfcMessageId, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT m.id FROM mail.messages m JOIN mail.message_labels ml ON ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND ml.label_id = @l
            WHERE m.tenant_id = @t AND m.mailbox_id = @mb AND m.rfc_message_id = @rid AND m.deleted_at IS NULL AND m.expunged_at IS NULL LIMIT 1
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("l", labelId);
        cmd.Parameters.AddWithValue("rid", rfcMessageId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>
    /// A message in the folder that is the same sent mail under another Message-ID: Outlook gives the copy it APPENDs to
    /// Sent a new Message-ID after submitting the original over SMTP (docs/08 BUG-080). Same Date (to the second), sender,
    /// subject and To+Cc addresses, stored within the last hour.
    /// </summary>
    public async Task<Guid?> ImapFindSentTwinAsync(MailboxAccess access, Guid labelId, string? from, string? subject, DateTimeOffset sentAt,
        IReadOnlyCollection<string> recipients, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT m.id FROM mail.messages m JOIN mail.message_labels ml ON ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND ml.label_id = @l
            WHERE m.tenant_id = @t AND m.mailbox_id = @mb AND m.deleted_at IS NULL AND m.expunged_at IS NULL
              AND m.created_at > now() - interval '1 hour' AND m.sent_at = @sent
              AND lower(coalesce(m.from_addr, '')) = @from AND coalesce(m.subject, '') = @subject
              AND (SELECT coalesce(array_agg(DISTINCT lower(e->>'address') ORDER BY lower(e->>'address')), '{}')
                   FROM jsonb_array_elements(m.to_addrs || m.cc_addrs) e) = @rcpt
            LIMIT 1
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("l", labelId);
        cmd.Parameters.AddWithValue("sent", sentAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("from", (from ?? "").ToLowerInvariant());
        cmd.Parameters.AddWithValue("subject", subject ?? "");
        cmd.Parameters.AddWithValue("rcpt", recipients.Select(r => r.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray());
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Envelope fields for IMAP SEARCH (FROM, TO, CC, BCC, SUBJECT, SENT*), from the indexed columns.</summary>
    public async Task<IReadOnlyDictionary<Guid, ImapSearchMeta>> ImapMetaAsync(MailboxAccess access, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, coalesce(from_name, '') || ' <' || coalesce(from_addr, '') || '>', to_addrs::text, cc_addrs::text, bcc_addrs::text, coalesce(subject, ''), sent_at
            FROM mail.messages WHERE tenant_id = @t AND mailbox_id = @mb AND id = ANY(@ids)
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        var map = new Dictionary<Guid, ImapSearchMeta>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            map[rd.GetGuid(0)] = new ImapSearchMeta(rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetFieldValue<DateTimeOffset>(6));
        }

        return map;
    }

    /// <summary>Messages whose indexed text (subject, people, file names, body) contains all the words (SEARCH BODY / TEXT).</summary>
    public async Task<IReadOnlySet<Guid>> ImapTextMatchAsync(MailboxAccess access, IReadOnlyCollection<Guid> ids, string text, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id FROM mail.messages WHERE tenant_id = @t AND mailbox_id = @mb AND id = ANY(@ids)
              AND (search_vector @@ plainto_tsquery('simple', public.f_unaccent(@q))
                   OR public.f_unaccent(lower(coalesce(subject, ''))) LIKE '%' || public.f_unaccent(lower(@q)) || '%')
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        cmd.Parameters.AddWithValue("q", text);
        var set = new HashSet<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            set.Add(rd.GetGuid(0));
        }

        return set;
    }

    public async Task SetSubscribedAsync(MailboxAccess access, Guid labelId, bool subscribed, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Write);
        await db.Labels.IgnoreQueryFilters().Where(l => l.Id == labelId && l.MailboxId == access.MailboxId && l.TenantId == access.TenantId)
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.ImapSubscribed, subscribed), ct);
    }

    public async Task<IReadOnlySet<Guid>> UnsubscribedAsync(MailboxAccess access, CancellationToken ct = default) =>
        (await db.Labels.IgnoreQueryFilters().Where(l => l.MailboxId == access.MailboxId && l.TenantId == access.TenantId && !l.ImapSubscribed && l.DeletedAt == null)
            .Select(l => l.Id).ToListAsync(ct)).ToHashSet();
}

public sealed record ImapSearchMeta(string From, string To, string Cc, string Bcc, string Subject, DateTimeOffset? SentAt);
