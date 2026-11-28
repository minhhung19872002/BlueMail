using System.Text.Json;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using StackExchange.Redis;

namespace BlueMail.MailStore;

/// <summary>
/// One copy to store. <see cref="DeliveredTo"/> are the envelope recipients that led to this mailbox (deliveredto:).
/// Starred is the STARRED system label (the flag mirrors it).
/// </summary>
public sealed record MailboxDelivery(Guid MailboxId, IReadOnlyList<string> SystemLabels, bool Seen = false, string? Category = null, bool Important = false,
    IReadOnlyList<Guid>? ExtraLabelIds = null, bool Draft = false, IReadOnlyList<string>? DeliveredTo = null)
{
    /// <summary>Imported mail keeps its source flags (PROMPT 7.7).</summary>
    public bool Answered { get; init; }
    public bool Forwarded { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

public sealed record DeliveryRequest(
    Guid TenantId,
    Stream Content,
    MessageFacts Facts,
    IReadOnlyList<MailboxDelivery> Targets,
    DateTimeOffset ReceivedAt,
    string? AuthResultsJson = null,
    double? SpamScore = null,
    string? SpamReportJson = null,
    IReadOnlyDictionary<string, VirusStatus>? AttachmentStatus = null);

public sealed record DeliveredMessage(Guid MailboxId, Guid MessageId, Guid ThreadId, long Modseq);

/// <summary>Change notifications (PROMPT principle 1): every write publishes mailbox id + modseq on Valkey.</summary>
public interface IMailEvents
{
    Task PublishAsync(Guid tenantId, Guid mailboxId, long modseq, string kind);
}

public sealed class ValkeyMailEvents(IConnectionMultiplexer valkey) : IMailEvents
{
    public const string ChannelPrefix = "bm:mbx:";

    public async Task PublishAsync(Guid tenantId, Guid mailboxId, long modseq, string kind) =>
        await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(ChannelPrefix + mailboxId.ToString("N")),
            JsonSerializer.Serialize(new { tenantId, mailboxId, modseq, kind }));
}

public sealed partial class PgMailStore
{
    public static readonly TimeSpan SubjectThreadWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// Delivers one message to every target mailbox atomically (PROMPT 2.1 step 8). The content is stored on S3 first,
    /// then all rows commit in one transaction; only after this returns may the SMTP layer answer 250.
    /// </summary>
    public async Task<IReadOnlyList<DeliveredMessage>> DeliverAsync(DeliveryRequest r, CancellationToken ct = default)
    {
        // PostgreSQL timestamptz parameters must be UTC; imports carry the source's own offset (BUG-052).
        r = r with { ReceivedAt = r.ReceivedAt.ToUniversalTime() };
        var sha = await BlobStore.HashAsync(r.Content, ct);
        await BlobRefs.EnsureAsync(db, blobs, r.TenantId, sha, r.Content, ct);

        var results = new List<DeliveredMessage>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgTx = (NpgsqlTransaction)tx.GetDbTransaction();
            await Exec(conn, npgTx, ct, """
                INSERT INTO mail.blobs (tenant_id, sha256, object_key, size, created_at) VALUES (@t, @sha, @key, @size, now())
                ON CONFLICT (tenant_id, sha256) DO NOTHING
                """, ("t", r.TenantId), ("sha", sha), ("key", BlobStore.ObjectKey(r.TenantId, sha)), ("size", r.Content.Length));

            foreach (var target in r.Targets.DistinctBy(t => t.MailboxId))
            {
                results.Add(await DeliverToMailboxAsync(conn, npgTx, r, sha, target, ct));
            }

            await tx.CommitAsync(ct);
        }

        foreach (var d in results)
        {
            await events.PublishAsync(r.TenantId, d.MailboxId, d.Modseq, "message.added");
        }

        return results;
    }

    private static async Task<DeliveredMessage> DeliverToMailboxAsync(NpgsqlConnection c, NpgsqlTransaction tx, DeliveryRequest r, byte[] sha, MailboxDelivery target, CancellationToken ct)
    {
        var f = r.Facts;
        // Row lock + version: concurrent deliveries to one mailbox are ordered by this UPDATE.
        var modseq = await Scalar<long?>(c, tx, ct, "UPDATE mail.mailboxes SET highest_modseq = highest_modseq + 1 WHERE id = @mb AND tenant_id = @t AND deleted_at IS NULL RETURNING highest_modseq",
                         ("mb", target.MailboxId), ("t", r.TenantId))
                     ?? throw new InvalidOperationException($"Mailbox {target.MailboxId} is not in tenant {r.TenantId}.");

        var threadId = await FindThreadAsync(c, tx, r, target.MailboxId, ct);
        if (threadId is null)
        {
            threadId = Guid.NewGuid();
            await Exec(c, tx, ct, """
                INSERT INTO mail.threads (id, tenant_id, mailbox_id, normalized_subject, last_message_at, muted, created_at, updated_at)
                VALUES (@id, @t, @mb, @ns, @at, false, now(), now())
                """, ("id", threadId.Value), ("t", r.TenantId), ("mb", target.MailboxId), ("ns", Trunc(f.NormalizedSubject, 998)), ("at", r.ReceivedAt));
        }
        else
        {
            await Exec(c, tx, ct, "UPDATE mail.threads SET last_message_at = GREATEST(last_message_at, @at), updated_at = now() WHERE id = @id",
                ("id", threadId.Value), ("at", r.ReceivedAt));
        }

        var messageId = Guid.NewGuid();
        var people = SearchText.People(f);
        var files = string.Join(' ', f.Attachments.Select(a => a.FileName));
        static string Addrs(IEnumerable<BlueMail.Mime.MailboxAddress> list) => JsonSerializer.Serialize(list.Select(a => new { name = a.Name, address = a.Address }));
        await Exec(c, tx, ct, """
            INSERT INTO mail.messages (tenant_id, id, mailbox_id, blob_sha256, thread_id, rfc_message_id, in_reply_to, "references", subject, normalized_subject,
                from_addr, from_name, to_addrs, cc_addrs, bcc_addrs, reply_to, sent_at, received_at, size, snippet, has_attachments, mime_structure,
                seen, flagged, answered, forwarded, draft, keywords, spam_score, spam_report, auth_results, list_id, list_unsubscribe, category, is_important, priority, mdn_to,
                modseq, legal_hold_count, search_vector, delivered_to, created_at, updated_at)
            VALUES (@t, @id, @mb, @sha, @thread, @mid, @irt, @refs, @subj, @ns, @from, @fromName, @to::jsonb, @cc::jsonb, @bcc::jsonb, @replyTo::jsonb,
                @sent, @recv, @size, @snippet, @hasAtt, @mime::jsonb, @seen, @flagged, @answered, @forwarded, @draft, @keywords, @spam, @spamReport::jsonb, @auth::jsonb,
                @listId, @unsub::jsonb, @category, @important, @priority, @mdnTo, @modseq, compliance.f_mailbox_hold_count(@t, @mb),
                setweight(to_tsvector('simple', public.f_unaccent(@vSubject)), 'A') ||
                setweight(to_tsvector('simple', public.f_unaccent(@vPeople)), 'B') ||
                setweight(to_tsvector('simple', public.f_unaccent(@vFiles)), 'B') ||
                setweight(to_tsvector('simple', public.f_unaccent(@vBody)), 'D'),
                @deliveredTo, now(), now())
            """,
            ("t", r.TenantId), ("id", messageId), ("mb", target.MailboxId), ("sha", sha), ("thread", threadId.Value),
            ("mid", (object?)Trunc(f.RfcMessageId, 998) ?? DBNull.Value), ("irt", (object?)Trunc(f.InReplyTo, 998) ?? DBNull.Value),
            ("refs", f.References.Select(x => Trunc(x, 998)!).ToArray()), ("subj", (object?)Trunc(f.Subject, 4000) ?? DBNull.Value),
            ("ns", Trunc(f.NormalizedSubject, 998)), ("from", (object?)Trunc(f.From?.Address, 320) ?? DBNull.Value),
            ("fromName", (object?)Trunc(f.From?.Name, 400) ?? DBNull.Value), ("to", Addrs(f.To)), ("cc", Addrs(f.Cc)), ("bcc", Addrs(f.Bcc)), ("replyTo", Addrs(f.ReplyTo)),
            ("sent", (object?)f.Date?.ToUniversalTime() ?? DBNull.Value), ("recv", r.ReceivedAt.ToUniversalTime()), ("size", r.Content.Length), ("snippet", f.Snippet),
            ("hasAtt", f.HasAttachments), ("mime", JsonSerializer.Serialize(f.Parts, MessageFacts.Json)), ("seen", target.Seen), ("draft", target.Draft),
            ("flagged", target.SystemLabels.Contains(SystemLabels.Starred)), ("answered", target.Answered), ("forwarded", target.Forwarded),
            ("keywords", target.Keywords.ToArray()),
            ("deliveredTo", (target.DeliveredTo ?? []).Select(a => a.ToLowerInvariant()).Distinct().ToArray()),
            ("spam", (object?)r.SpamScore ?? DBNull.Value), ("spamReport", (object?)r.SpamReportJson ?? DBNull.Value), ("auth", (object?)r.AuthResultsJson ?? DBNull.Value),
            ("listId", (object?)Trunc(f.ListId, 998) ?? DBNull.Value),
            ("unsub", f.ListUnsubscribe is null ? DBNull.Value : JsonSerializer.Serialize(new { header = f.ListUnsubscribe, post = f.ListUnsubscribePost })),
            ("category", (object?)target.Category ?? DBNull.Value), ("important", target.Important), ("modseq", modseq),
            ("priority", f.Priority), ("mdnTo", (object?)f.DispositionNotificationTo ?? DBNull.Value),
            ("vSubject", SearchText.Prepare(f.Subject, 4000)), ("vPeople", SearchText.Prepare(people, 20_000)), ("vFiles", SearchText.Prepare(files, 20_000)),
            ("vBody", SearchText.Prepare(f.BodyText)));

        // Labels: system keys of this mailbox plus explicit label ids, each with a fresh UID.
        var labelIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM mail.labels WHERE tenant_id = @t AND mailbox_id = @mb AND deleted_at IS NULL AND (system_key = ANY(@keys) OR id = ANY(@ids))", c, tx))
        {
            cmd.Parameters.AddWithValue("t", r.TenantId);
            cmd.Parameters.AddWithValue("mb", target.MailboxId);
            cmd.Parameters.AddWithValue("keys", target.SystemLabels.ToArray());
            cmd.Parameters.AddWithValue("ids", (target.ExtraLabelIds ?? []).ToArray());
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                labelIds.Add(rd.GetGuid(0));
            }
        }

        foreach (var labelId in labelIds)
        {
            var uid = await Scalar<long>(c, tx, ct, "UPDATE mail.labels SET uid_next = uid_next + 1 WHERE id = @l RETURNING uid_next - 1", ("l", labelId));
            await Exec(c, tx, ct, "INSERT INTO mail.message_labels (tenant_id, message_id, label_id, imap_uid, modseq) VALUES (@t, @m, @l, @uid, @modseq)",
                ("t", r.TenantId), ("m", messageId), ("l", labelId), ("uid", uid), ("modseq", modseq));
            await Exec(c, tx, ct, "INSERT INTO mail.label_changes (tenant_id, label_id, imap_uid, modseq, change, occurred_at) VALUES (@t, @l, @uid, @modseq, 'ADDED', now())",
                ("t", r.TenantId), ("l", labelId), ("uid", uid), ("modseq", modseq));
        }

        foreach (var a in f.Attachments)
        {
            var status = r.AttachmentStatus?.GetValueOrDefault(a.PartPath) ?? VirusStatus.Unscanned;
            await Exec(c, tx, ct, """
                INSERT INTO mail.attachments (id, tenant_id, message_id, part_path, file_name, content_type, size, content_id, is_inline, virus_status, sha256, created_at, updated_at)
                VALUES (gen_random_uuid(), @t, @m, @path, @name, @type, @size, @cid, @inline, @status, @sha, now(), now())
                """, ("t", r.TenantId), ("m", messageId), ("path", a.PartPath), ("name", (object?)Trunc(a.FileName, 1024) ?? DBNull.Value),
                ("type", Trunc(a.ContentType, 255)!), ("size", a.Size), ("cid", (object?)Trunc(a.ContentId, 998) ?? DBNull.Value), ("inline", a.Inline),
                ("status", status.ToString().ToUpperInvariant()), ("sha", Array.Empty<byte>()));
        }

        return new DeliveredMessage(target.MailboxId, messageId, threadId.Value, modseq);
    }

    /// <summary>Threading within one mailbox (PROMPT 2.5): References/In-Reply-To, replies that arrived first, then subject.</summary>
    private static async Task<Guid?> FindThreadAsync(NpgsqlConnection c, NpgsqlTransaction tx, DeliveryRequest r, Guid mailbox, CancellationToken ct)
    {
        var f = r.Facts;
        var ids = f.References.Concat(f.InReplyTo is null ? [] : new[] { f.InReplyTo }).Distinct().ToArray();
        if (ids.Length > 0)
        {
            var t = await Scalar<Guid?>(c, tx, ct, """
                SELECT thread_id FROM mail.messages WHERE tenant_id = @t AND mailbox_id = @mb AND rfc_message_id = ANY(@ids) AND deleted_at IS NULL
                ORDER BY received_at DESC LIMIT 1
                """, ("t", r.TenantId), ("mb", mailbox), ("ids", ids));
            if (t is not null)
            {
                return t;
            }
        }

        if (f.RfcMessageId is not null)
        {
            var t = await Scalar<Guid?>(c, tx, ct, """
                SELECT thread_id FROM mail.messages WHERE tenant_id = @t AND mailbox_id = @mb AND deleted_at IS NULL AND (in_reply_to = @mid OR @mid = ANY("references"))
                ORDER BY received_at DESC LIMIT 1
                """, ("t", r.TenantId), ("mb", mailbox), ("mid", f.RfcMessageId));
            if (t is not null)
            {
                return t;
            }
        }

        if (f.SubjectHadPrefix && f.NormalizedSubject.Length >= 3)
        {
            return await Scalar<Guid?>(c, tx, ct, """
                SELECT id FROM mail.threads WHERE tenant_id = @t AND mailbox_id = @mb AND normalized_subject = @ns AND last_message_at > @since AND deleted_at IS NULL
                ORDER BY last_message_at DESC LIMIT 1
                """, ("t", r.TenantId), ("mb", mailbox), ("ns", Trunc(f.NormalizedSubject, 998)), ("since", r.ReceivedAt - SubjectThreadWindow));
        }

        return null;
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

#pragma warning disable CA1068 // params must come last; the token precedes the SQL for readability
    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }
#pragma warning restore CA1068
}
