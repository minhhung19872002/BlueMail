using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueMail.Domain.Mail;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>Which messages a list shows. A label, or one of the virtual views.</summary>
public enum MailView { Label, AllMail }

/// <summary>
/// A list request. With <see cref="Search"/> the view's label still applies (search inside a label); in the
/// All Mail view Spam and Trash stay hidden unless the query names them (in:spam, in:trash, in:anywhere).
/// </summary>
public sealed record ConversationQuery(
    MailView View,
    Guid? LabelId,
    bool UnreadOnly = false,
    bool Threads = true,
    string? Cursor = null,
    int Limit = 50,
    Search.SearchNode? Search = null,
    IReadOnlyList<string>? MyAddresses = null,
    string Sort = ConversationSort.Date,
    bool Ascending = false);

/// <summary>
/// List orders (Outlook "Sort by"). Date is the keyset-paged default; the others sort conversations in memory and so
/// only apply to lists the store can hold exactly (at most CountCap messages); bigger lists stay by date (docs/00 D30).
/// </summary>
public static class ConversationSort
{
    public const string Date = "date", From = "from", Subject = "subject", Size = "size";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Date, From, Subject, Size };
}

public sealed record Participant(string? Name, string Address, bool Unread);

public sealed record ConversationSummary(
    Guid ThreadId,
    Guid LatestMessageId,
    DateTimeOffset LastMessageAt,
    string? Subject,
    string? Snippet,
    IReadOnlyList<Participant> Participants,
    int MessageCount,
    int UnreadCount,
    bool Starred,
    bool Important,
    bool HasAttachments,
    IReadOnlyList<string> AttachmentNames,
    IReadOnlyList<Guid> LabelIds,
    bool Muted,
    short Priority = MessagePriority.Normal);

/// <summary>A page of a list. <see cref="Total"/> is exact when <see cref="TotalExact"/>, otherwise a lower bound.</summary>
public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, string? NextCursor, long Total, bool TotalExact = true, string Sort = ConversationSort.Date);

public sealed record AddressDto(string? Name, string Address);

public sealed record AttachmentInfo(Guid Id, string PartPath, string? FileName, string ContentType, long Size, string? ContentId, bool Inline, VirusStatus VirusStatus);

public sealed record MessageView(
    Guid Id,
    Guid ThreadId,
    string? RfcMessageId,
    string? Subject,
    AddressDto? From,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    IReadOnlyList<AddressDto> ReplyTo,
    DateTimeOffset? SentAt,
    DateTimeOffset ReceivedAt,
    long Size,
    string? Snippet,
    bool Seen,
    bool Starred,
    bool Important,
    bool Draft,
    IReadOnlyList<Guid> LabelIds,
    IReadOnlyList<AttachmentInfo> Attachments,
    string? AuthResults,
    string? SpamReport,
    double? SpamScore,
    string? ListUnsubscribe,
    long Modseq,
    short Priority = MessagePriority.Normal,
    string? ReadReceiptTo = null,
    bool ReadReceiptSent = false);

public sealed record ThreadView(Guid ThreadId, string? Subject, bool Muted, IReadOnlyList<MessageView> Messages);

/// <summary>Displayable bodies of one message. HTML is raw here; the web layer sanitizes it.</summary>
public sealed record MessageContent(string? Html, string? Text, IReadOnlyDictionary<string, string> ContentIds, string? Calendar);

public sealed record MessageChangeEntry(Guid Id, Guid ThreadId, long Modseq, bool Seen, bool Starred, IReadOnlyList<Guid> LabelIds);

/// <summary>Changes since a modseq (PROMPT 5, incremental sync).</summary>
public sealed record MailChanges(long HighestModseq, IReadOnlyList<MessageChangeEntry> Changed, IReadOnlyList<Guid> Removed, bool TooOld);

public sealed partial class PgMailStore
{
    public const int MaxPageSize = 100;

    private static readonly string[] HiddenFromAllMail = [SystemLabels.Trash, SystemLabels.Spam];

    private NpgsqlConnection Conn() => (NpgsqlConnection)db.Database.GetDbConnection();

    private async Task<NpgsqlConnection> OpenConnAsync(CancellationToken ct)
    {
        var c = Conn();
        if (c.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return c;
    }

    public async Task<ConversationPage> ListConversationsAsync(MailboxAccess access, ConversationQuery q, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var limit = Math.Clamp(q.Limit, 1, MaxPageSize);
        var c = await OpenConnAsync(ct);
        var key = q.Threads ? "m.thread_id" : "m.id";
        var labelFilter = q.View == MailView.Label
            ? "AND EXISTS (SELECT 1 FROM mail.message_labels ml WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND ml.label_id = @label)"
            : Search.SearchParser.LooksInSpamOrTrash(q.Search)
                ? ""
                : """
                  AND NOT EXISTS (SELECT 1 FROM mail.message_labels ml JOIN mail.labels l ON l.id = ml.label_id
                                  WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND l.system_key = ANY(@hidden))
                  """;
        var (searchSql, searchParams) = q.Search is null ? ("true", []) : Search.SearchSql.Translate(q.Search, q.MyAddresses ?? []);
        var where = $"m.tenant_id = @t AND m.mailbox_id = @mb AND m.deleted_at IS NULL AND m.expunged_at IS NULL {labelFilter} AND {searchSql}";

        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("t", access.TenantId);
            cmd.Parameters.AddWithValue("mb", access.MailboxId);
            cmd.Parameters.AddWithValue("label", q.LabelId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("hidden", HiddenFromAllMail);
            foreach (var p in searchParams)
            {
                cmd.Parameters.Add(p.Clone());
            }
        }

        // One read of at most CountCap + 1 matching messages, in whatever order the indexes give them (GIN, trigram,
        // label). Up to CountCap the list is grouped right here with an exact total; above it the total is "more than
        // CountCap" and the page comes from walking messages newest first (PROMPT 8.2; counting or grouping every
        // conversation of a huge label on each view, or walking 1M messages for a rare phrase, is what was slow).
        var hits = new List<(Guid Id, Guid Key, DateTimeOffset At, bool Seen)>();
        var sort = ConversationSort.All.Contains(q.Sort) ? q.Sort : ConversationSort.Date;
        var byOther = sort != ConversationSort.Date;
        var extra = new Dictionary<Guid, (string From, string Subject, long Size)>();

        // A big label without a query: its (cached, exact) message count already says "more than CountCap", so the
        // read below would only be thrown away.
        var knownBig = q.View == MailView.Label && q.Search is null && q.LabelId is { } lid
                       && (await LabelCountsAsync(access, ct)).TryGetValue(lid, out var lc) && lc.Total > CountCap;
        if (!knownBig)
        {
            var columns = byOther ? ", lower(coalesce(nullif(m.from_name, ''), m.from_addr, '')), lower(coalesce(m.normalized_subject, '')), m.size" : "";
            await using var cmd = new NpgsqlCommand($"SELECT m.id, {key}, m.received_at, m.seen{columns} FROM mail.messages m WHERE {where} LIMIT {CountCap + 1}", c);
            Bind(cmd);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                hits.Add((rd.GetGuid(0), rd.GetGuid(1), rd.GetFieldValue<DateTimeOffset>(2), rd.GetBoolean(3)));
                if (byOther)
                {
                    extra[rd.GetGuid(0)] = (rd.GetString(4), rd.GetString(5), rd.GetInt64(6));
                }
            }
        }

        var exact = !knownBig && hits.Count <= CountCap;
        long total = CountCap;
        if (!exact)
        {
            sort = ConversationSort.Date; // too big to sort in memory: newest first, the caller is told
        }

        // Other orders page by position ("o:<offset>"); date keeps its keyset cursor.
        var offsetPaging = sort != ConversationSort.Date;
        var offset = offsetPaging && q.Cursor is { } oc && oc.StartsWith("o:", StringComparison.Ordinal)
                     && int.TryParse(oc.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 0;

        // Page order: conversations by their newest message (time, then id in PostgreSQL's uuid order). The cursor is
        // that newest message of the last conversation shown; a conversation with a match at or above it was shown.
        var (cursorAt, cursorId) = offsetPaging || (q.Cursor?.StartsWith("o:", StringComparison.Ordinal) ?? false) ? default : DecodeCursor(q.Cursor);
        var page = new List<(Guid Key, DateTimeOffset Last, int Count, int Unread, DateTimeOffset FirstAt, Guid FirstId)>();
        var met = new HashSet<Guid>();
        var (afterAt, afterId) = (cursorAt, cursorId);
        var chunk = Math.Max(200, limit * 8);
        var exhausted = false;
        if (exact)
        {
            exhausted = true;
            var groups = hits.GroupBy(h => h.Key).Select(g =>
                {
                    var top = g.OrderByDescending(h => h.At).ThenByDescending(h => h.Id, UuidOrder.Instance).First();
                    return (Key: g.Key, Last: top.At, TopId: top.Id, Count: g.Count(), Unread: g.Count(h => !h.Seen));
                })
                .Where(x => !q.UnreadOnly || x.Unread > 0)
                .ToList();
            total = groups.Count;
            if (offsetPaging)
            {
                var sizes = sort == ConversationSort.Size ? hits.GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.Sum(h => extra[h.Id].Size)) : null;
                // Sort key of a conversation: its newest message's sender or subject, or the size of all its matches.
                IComparable Key((Guid Key, DateTimeOffset Last, Guid TopId, int Count, int Unread) g) => sort switch
                {
                    ConversationSort.From => extra[g.TopId].From,
                    ConversationSort.Subject => extra[g.TopId].Subject,
                    _ => sizes![g.Key],
                };
                var keyed = groups.Select(g => (g, k: Key(g))).ToList();
                var sorted = q.Ascending
                    ? keyed.OrderBy(x => x.k)
                    : keyed.OrderByDescending(x => x.k);
                page.AddRange(sorted.ThenByDescending(x => x.g.Last).ThenByDescending(x => x.g.TopId, UuidOrder.Instance)
                    .Skip(offset).Take(limit + 1).Select(x => (x.g.Key, x.g.Last, x.g.Count, x.g.Unread, x.g.Last, x.g.TopId)));
            }
            else
            {
            page.AddRange(groups
                .Where(x => cursorAt is not { } cAt || x.Last < cAt || (x.Last == cAt && UuidOrder.Instance.Compare(x.TopId, cursorId) < 0))
                .OrderByDescending(x => x.Last).ThenByDescending(x => x.TopId, UuidOrder.Instance)
                .Take(limit + 1)
                .Select(x => (x.Key, x.Last, x.Count, x.Unread, x.Last, x.TopId)));
            }
        }

        while (page.Count <= limit && !exhausted)
        {
            var fresh = new List<(Guid Key, DateTimeOffset At, Guid Id)>();
            var rows = 0;
            await using (var cmd = new NpgsqlCommand($"""
                SELECT m.id, {key}, m.received_at FROM mail.messages m
                WHERE {where} AND (@aAt IS NULL OR (m.received_at, m.id) < (@aAt, @aId))
                ORDER BY m.received_at DESC, m.id DESC
                LIMIT @chunk
                """, c))
            {
                Bind(cmd);
                cmd.Parameters.Add(new NpgsqlParameter("aAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)afterAt ?? DBNull.Value });
                cmd.Parameters.AddWithValue("aId", afterId);
                cmd.Parameters.AddWithValue("chunk", chunk);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    rows++;
                    var (id, k, at) = (rd.GetGuid(0), rd.GetGuid(1), rd.GetFieldValue<DateTimeOffset>(2));
                    (afterAt, afterId) = (at, id);
                    if (met.Add(k))
                    {
                        fresh.Add((k, at, id));
                    }
                }
            }

            exhausted = rows < chunk;
            if (fresh.Count == 0)
            {
                continue;
            }

            // Counts of the whole conversation (all its matching messages), and whether it was on an earlier page.
            var stats = new Dictionary<Guid, (DateTimeOffset Last, int Count, int Unread)>();
            await using (var cmd = new NpgsqlCommand($"""
                SELECT {key}, max(m.received_at), count(*)::int, (count(*) FILTER (WHERE NOT m.seen))::int
                FROM mail.messages m WHERE {where} AND {key} = ANY(@keys) GROUP BY {key}
                """, c))
            {
                Bind(cmd);
                cmd.Parameters.AddWithValue("keys", fresh.Select(f => f.Key).ToArray());
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    stats[rd.GetGuid(0)] = (rd.GetFieldValue<DateTimeOffset>(1), rd.GetInt32(2), rd.GetInt32(3));
                }
            }

            foreach (var f in fresh)
            {
                if (!stats.TryGetValue(f.Key, out var st) || (cursorAt is { } cAt && st.Last >= cAt) || (q.UnreadOnly && st.Unread == 0))
                {
                    continue;
                }

                page.Add((f.Key, st.Last, st.Count, st.Unread, f.At, f.Id));
                if (page.Count > limit)
                {
                    break;
                }
            }
        }

        string? next = null;
        if (page.Count > limit)
        {
            page.RemoveRange(limit, page.Count - limit);
            next = offsetPaging
                ? "o:" + (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : EncodeCursor(page[^1].FirstAt, page[^1].FirstId);
        }

        var items = await SummariesAsync(access, page.Select(p => p.Key).ToArray(), q, ct);
        var ordered = page.Select(p => items.TryGetValue(p.Key, out var s) ? s with { MessageCount = p.Count, UnreadCount = p.Unread, LastMessageAt = p.Last } : null)
            .Where(s => s is not null).Select(s => s!).ToList();
        return new ConversationPage(ordered, next, total, exact, sort);
    }

    /// <summary>Above this many matching messages a list reports "more than" instead of counting (PROMPT 8.2).</summary>
    public const int DefaultCountCap = 10_000;

    /// <summary>Test hook: a small cap exercises the newest-first walk with few messages.</summary>
    public int CountCap { get; set; } = DefaultCountCap;

    /// <summary>uuid ordering as PostgreSQL compares it (bytes in RFC 4122 order), for cursors built in memory.</summary>
    private sealed class UuidOrder : IComparer<Guid>
    {
        public static readonly UuidOrder Instance = new();

        public int Compare(Guid x, Guid y) => string.CompareOrdinal(x.ToString("N"), y.ToString("N"));
    }

    /// <summary>
    /// Ids of messages matching a query, newest first, after <paramref name="before"/> (keyset paging). Spam and
    /// Trash are left out unless the query names them, as in the list.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, DateTimeOffset At)>> SearchMessagesAsync(MailboxAccess access, Search.SearchNode node, IReadOnlyList<string> me,
        (DateTimeOffset At, Guid Id)? before, int limit, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var c = await OpenConnAsync(ct);
        var (where, ps) = Search.SearchSql.Translate(node, me);
        var hidden = Search.SearchParser.LooksInSpamOrTrash(node) ? "" : """
            AND NOT EXISTS (SELECT 1 FROM mail.message_labels ml JOIN mail.labels l ON l.id = ml.label_id
                            WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND l.system_key = ANY(@hidden))
            """;
        await using var cmd = new NpgsqlCommand($"""
            SELECT m.id, m.received_at FROM mail.messages m
            WHERE m.tenant_id = @t AND m.mailbox_id = @mb AND m.deleted_at IS NULL AND m.expunged_at IS NULL {hidden} AND {where}
              AND (@bAt IS NULL OR (m.received_at, m.id) < (@bAt, @bId))
            ORDER BY m.received_at DESC, m.id DESC LIMIT @lim
            """, c);
        cmd.Parameters.AddWithValue("t", access.TenantId);
        cmd.Parameters.AddWithValue("mb", access.MailboxId);
        cmd.Parameters.AddWithValue("hidden", HiddenFromAllMail);
        cmd.Parameters.Add(new NpgsqlParameter("bAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = before is { } b1 ? b1.At : DBNull.Value });
        cmd.Parameters.AddWithValue("bId", before?.Id ?? Guid.Empty);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 5000));
        foreach (var p in ps)
        {
            cmd.Parameters.Add(p.Clone());
        }

        var list = new List<(Guid, DateTimeOffset)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add((rd.GetGuid(0), rd.GetFieldValue<DateTimeOffset>(1)));
        }

        return list;
    }

    /// <summary>Details of the listed rows: latest message, participants, labels, attachment names.</summary>
    private async Task<Dictionary<Guid, ConversationSummary>> SummariesAsync(MailboxAccess access, Guid[] keys, ConversationQuery q, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ConversationSummary>();
        if (keys.Length == 0)
        {
            return result;
        }

        var c = await OpenConnAsync(ct);
        var key = q.Threads ? "thread_id" : "id";
        var rows = new List<(Guid Key, Guid Id, DateTimeOffset At, string? Subject, string? Snippet, string? FromName, string? FromAddr, bool Seen, bool Flagged,
            bool Important, bool HasAtt, Guid[] Labels, bool Muted, short Priority)>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT m.{key}, m.id, m.received_at, m.subject, m.snippet, m.from_name, m.from_addr, m.seen, m.flagged, m.is_important, m.has_attachments,
                   ARRAY(SELECT ml.label_id FROM mail.message_labels ml WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id),
                   coalesce((SELECT t.muted FROM mail.threads t WHERE t.id = m.thread_id), false), m.priority
            FROM mail.messages m
            WHERE m.tenant_id = @t AND m.mailbox_id = @mb AND m.{key} = ANY(@keys) AND m.deleted_at IS NULL AND m.expunged_at IS NULL
            ORDER BY m.received_at
            """, c))
        {
            cmd.Parameters.AddWithValue("t", access.TenantId);
            cmd.Parameters.AddWithValue("mb", access.MailboxId);
            cmd.Parameters.AddWithValue("keys", keys);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add((rd.GetGuid(0), rd.GetGuid(1), rd.GetFieldValue<DateTimeOffset>(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                    rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6), rd.GetBoolean(7), rd.GetBoolean(8), rd.GetBoolean(9),
                    rd.GetBoolean(10), rd.GetFieldValue<Guid[]>(11), rd.GetBoolean(12), rd.GetInt16(13)));
            }
        }

        var ids = rows.Select(r => r.Id).ToArray();
        var names = new Dictionary<Guid, List<string>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT message_id, file_name FROM mail.attachments
            WHERE tenant_id = @t AND message_id = ANY(@ids) AND NOT is_inline AND file_name IS NOT NULL AND deleted_at IS NULL ORDER BY file_name
            """, c))
        {
            cmd.Parameters.AddWithValue("t", access.TenantId);
            cmd.Parameters.AddWithValue("ids", ids);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                (names.TryGetValue(id, out var l) ? l : names[id] = []).Add(rd.GetString(1));
            }
        }

        foreach (var g in rows.GroupBy(r => r.Key))
        {
            var latest = g.Last();
            var participants = g.Where(r => r.FromAddr is not null)
                .GroupBy(r => r.FromAddr!, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Participant(p.Last().FromName, p.Key, p.Any(r => !r.Seen)))
                .ToList();
            result[g.Key] = new ConversationSummary(
                g.Key, latest.Id, latest.At, latest.Subject, latest.Snippet, participants, g.Count(), g.Count(r => !r.Seen),
                g.Any(r => r.Flagged), g.Any(r => r.Important), g.Any(r => r.HasAtt),
                g.SelectMany(r => names.GetValueOrDefault(r.Id) ?? []).Distinct().Take(5).ToList(),
                g.SelectMany(r => r.Labels).Distinct().ToList(), latest.Muted,
                // Like Outlook's list: "!" when any message is high importance, the low arrow when the latest one is low.
                g.Any(r => r.Priority == MessagePriority.High) ? MessagePriority.High : latest.Priority == MessagePriority.Low ? MessagePriority.Low : MessagePriority.Normal);
        }

        return result;
    }

    private static string EncodeCursor(DateTimeOffset at, Guid id)
    {
        var raw = at.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString("N");
        return Base64Url.EncodeToString(Encoding.ASCII.GetBytes(raw));
    }

    /// <summary>A malformed cursor reads as "from the start" rather than failing (it is opaque client input).</summary>
    private static (DateTimeOffset? At, Guid Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, Guid.Empty);
        }

        try
        {
            var parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks is > 0 and < 3155378975999999999 && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException)
        {
        }

        return (null, Guid.Empty);
    }

    public async Task<ThreadView?> GetThreadAsync(MailboxAccess access, Guid threadId, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var thread = await db.Threads.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == access.TenantId && t.MailboxId == access.MailboxId && t.Id == threadId && t.DeletedAt == null)
            .Select(t => new { t.Id, t.Muted }).FirstOrDefaultAsync(ct);
        if (thread is null)
        {
            return null;
        }

        var messages = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.ThreadId == threadId && m.DeletedAt == null && m.ExpungedAt == null)
            .OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
        if (messages.Count == 0)
        {
            return null;
        }

        var views = await ViewsAsync(access, messages, ct);
        return new ThreadView(thread.Id, messages[^1].Subject, thread.Muted, views);
    }

    public async Task<MessageView?> GetMessageAsync(MailboxAccess access, Guid messageId, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var m = await FindMessageAsync(access, messageId, ct);
        return m is null ? null : (await ViewsAsync(access, [m], ct))[0];
    }

    private Task<Message?> FindMessageAsync(MailboxAccess access, Guid messageId, CancellationToken ct) =>
        db.Messages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.Id == messageId && m.DeletedAt == null && m.ExpungedAt == null, ct);

    private async Task<List<MessageView>> ViewsAsync(MailboxAccess access, List<Message> messages, CancellationToken ct)
    {
        var ids = messages.Select(m => m.Id).ToList();
        var labels = await db.MessageLabels.IgnoreQueryFilters().AsNoTracking()
            .Where(ml => ml.TenantId == access.TenantId && ids.Contains(ml.MessageId))
            .Select(ml => new { ml.MessageId, ml.LabelId }).ToListAsync(ct);
        var starred = await SystemLabelIdAsync(access, SystemLabels.Starred, ct);
        var attachments = await db.Attachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == access.TenantId && ids.Contains(a.MessageId) && a.DeletedAt == null)
            .OrderBy(a => a.PartPath).ToListAsync(ct);
        return messages.Select(m =>
        {
            var lids = labels.Where(l => l.MessageId == m.Id).Select(l => l.LabelId).ToList();
            return new MessageView(m.Id, m.ThreadId, m.RfcMessageId, m.Subject, m.FromAddr is null ? null : new AddressDto(m.FromName, m.FromAddr),
                Addresses(m.ToAddrs), Addresses(m.CcAddrs), Addresses(m.ReplyTo), m.SentAt, m.ReceivedAt, m.Size, m.Snippet, m.Seen,
                m.Flagged || (starred is { } s && lids.Contains(s)), m.IsImportant, m.Draft, lids,
                attachments.Where(a => a.MessageId == m.Id)
                    .Select(a => new AttachmentInfo(a.Id, a.PartPath, a.FileName, a.ContentType, a.Size, a.ContentId, a.IsInline, a.VirusStatus)).ToList(),
                m.AuthResults, m.SpamReport, m.SpamScore, m.ListUnsubscribe, m.Modseq, m.Priority, m.MdnTo, m.Keywords.Contains(ReadReceipts.SentKeyword, StringComparer.OrdinalIgnoreCase));
        }).ToList();
    }

    private static List<AddressDto> Addresses(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : doc.RootElement.EnumerateArray()
                    .Where(e => e.TryGetProperty("address", out _))
                    .Select(e => new AddressDto(e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null, e.GetProperty("address").GetString() ?? ""))
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task<Guid?> SystemLabelIdAsync(MailboxAccess access, string key, CancellationToken ct) =>
        db.Labels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == access.TenantId && l.MailboxId == access.MailboxId && l.SystemKey == key && l.DeletedAt == null)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);

    /// <summary>Raw RFC 5322 bytes of a message (decrypted) into <paramref name="output"/>; false if not in the mailbox.</summary>
    public async Task<bool> CopyRawAsync(MailboxAccess access, Guid messageId, Stream output, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var m = await FindMessageAsync(access, messageId, ct);
        if (m is null)
        {
            return false;
        }

        await blobs.ReadAsync(access.TenantId, m.BlobSha256, output, ct: ct);
        return true;
    }

    /// <summary>Upper bound for loading a message into memory to render it; larger ones show the text part only from the raw stream.</summary>
    public const long MaxRenderBytes = 64L * 1024 * 1024;

    private async Task<(Message Row, MemoryStream Raw, MimeMessage Parsed)?> LoadAsync(MailboxAccess access, Guid messageId, CancellationToken ct)
    {
        var m = await FindMessageAsync(access, messageId, ct);
        if (m is null || m.Size > MaxRenderBytes)
        {
            return null;
        }

        var raw = new MemoryStream();
        await blobs.ReadAsync(access.TenantId, m.BlobSha256, raw, ct: ct);
        raw.Position = 0;
        return (m, raw, MimeMessage.Parse(raw));
    }

    public async Task<MessageContent?> GetContentAsync(MailboxAccess access, Guid messageId, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var loaded = await LoadAsync(access, messageId, ct);
        if (loaded is null)
        {
            return null;
        }

        var (_, raw, parsed) = loaded.Value;
        using (raw)
        {
            MimeEntity? html = null, text = null, calendar = null;
            var cids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in parsed.Root.WalkOwn().Where(e => e.IsLeaf))
            {
                if (e.ContentId is { } cid)
                {
                    cids.TryAdd(cid, e.PartPath);
                }

                if (e.IsAttachment)
                {
                    continue;
                }

                if (e.MediaType == "text/html")
                {
                    html ??= e;
                }
                else if (e.MediaType == "text/plain")
                {
                    text ??= e;
                }
                else if (e.MediaType == "text/calendar")
                {
                    calendar ??= e;
                }
            }

            string? Read(MimeEntity? e)
            {
                if (e is null)
                {
                    return null;
                }

                try
                {
                    return SearchText.Clean(e.ReadText(raw));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or DecoderFallbackException)
                {
                    return null;
                }
            }

            var plain = Read(text);
            if (plain is not null && text!.ContentType["format"]?.Equals("flowed", StringComparison.OrdinalIgnoreCase) == true)
            {
                plain = FormatFlowed.Decode(plain, text.ContentType["delsp"]?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true);
            }

            return new MessageContent(Read(html), plain ?? (html is null ? SearchText.Clean(parsed.BodyText(raw)) : null), cids, Read(calendar));
        }
    }

    /// <summary>Decoded content of one MIME part (attachment or inline image). Null when the part does not exist.</summary>
    public async Task<(AttachmentInfo Info, byte[] Content)?> GetPartAsync(MailboxAccess access, Guid messageId, string partPath, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        var loaded = await LoadAsync(access, messageId, ct);
        if (loaded is null)
        {
            return null;
        }

        var (row, raw, parsed) = loaded.Value;
        using (raw)
        {
            var part = parsed.Root.Walk().FirstOrDefault(e => (e.IsLeaf || e.IsAttachedMessage) && e.PartPath == partPath);
            if (part is null)
            {
                return null;
            }

            var att = await db.Attachments.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(a => a.TenantId == access.TenantId && a.MessageId == row.Id && a.PartPath == partPath && a.DeletedAt == null, ct);
            var info = att is null
                ? new AttachmentInfo(Guid.Empty, partPath, part.FileName, part.MediaType, part.BodyLength, part.ContentId, part.IsInline, VirusStatus.Unscanned)
                : new AttachmentInfo(att.Id, att.PartPath, att.FileName, att.ContentType, att.Size, att.ContentId, att.IsInline, att.VirusStatus);
            return (info, part.ReadDecodedBody(raw));
        }
    }

    public async Task<MailChanges> GetChangesAsync(MailboxAccess access, long sinceModseq, CancellationToken ct = default)
    {
        access.Require(MailboxRights.Read);
        const int Max = 2000;
        var highest = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.Id == access.MailboxId).Select(m => m.HighestModseq).FirstAsync(ct);
        var rows = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.Modseq > sinceModseq)
            .OrderBy(m => m.Modseq).Take(Max + 1)
            .Select(m => new { m.Id, m.ThreadId, m.Modseq, m.Seen, m.Flagged, Gone = m.ExpungedAt != null || m.DeletedAt != null })
            .ToListAsync(ct);
        if (rows.Count > Max)
        {
            // Too much changed: the client reloads its lists instead of applying thousands of deltas.
            return new MailChanges(highest, [], [], TooOld: true);
        }

        var ids = rows.Where(r => !r.Gone).Select(r => r.Id).ToList();
        var labels = await db.MessageLabels.IgnoreQueryFilters().AsNoTracking()
            .Where(ml => ml.TenantId == access.TenantId && ids.Contains(ml.MessageId))
            .Select(ml => new { ml.MessageId, ml.LabelId }).ToListAsync(ct);
        var starred = await SystemLabelIdAsync(access, SystemLabels.Starred, ct);
        var changed = rows.Where(r => !r.Gone).Select(r =>
        {
            var l = labels.Where(x => x.MessageId == r.Id).Select(x => x.LabelId).ToList();
            return new MessageChangeEntry(r.Id, r.ThreadId, r.Modseq, r.Seen, r.Flagged || (starred is { } s && l.Contains(s)), l);
        }).ToList();
        return new MailChanges(highest, changed, rows.Where(r => r.Gone).Select(r => r.Id).ToList(), false);
    }
}
