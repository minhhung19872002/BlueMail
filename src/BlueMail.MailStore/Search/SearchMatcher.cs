using System.Globalization;
using System.Text;
using BlueMail.Mime;
using Npgsql;

namespace BlueMail.MailStore.Search;

/// <summary>
/// Case and accent folding: must give the same result as <c>public.f_unaccent(lower(x))</c> in PostgreSQL, which
/// is what the SQL side uses. <see cref="PgTextFolder"/> asks PostgreSQL itself; <see cref="LatinTextFolder"/>
/// is a local approximation for unit tests.
/// </summary>
public interface ITextFolder
{
    string Fold(string s);
}

/// <summary>Lower case and Latin diacritics removed (đ → d, ß → ss...). Close to PostgreSQL unaccent for Latin script.</summary>
public sealed class LatinTextFolder : ITextFolder
{
    public static readonly LatinTextFolder Instance = new();

    private static readonly Dictionary<char, string> Special = new()
    {
        ['đ'] = "d", ['ð'] = "d", ['ø'] = "o", ['ł'] = "l", ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['þ'] = "th", ['ħ'] = "h", ['ı'] = "i",
    };

    public string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var lastBase = '\0';
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark && lastBase < 'ɐ')
            {
                continue; // a mark on a Latin letter
            }

            lastBase = c;
            sb.Append(Special.TryGetValue(c, out var r) ? r : c.ToString());
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Folds a batch of strings with PostgreSQL in one round trip, then answers from memory.</summary>
public sealed class PgTextFolder : ITextFolder
{
    private readonly Dictionary<string, string> _folded;

    private PgTextFolder(Dictionary<string, string> folded) => _folded = folded;

    public static async Task<PgTextFolder> CreateAsync(NpgsqlConnection c, IEnumerable<string> texts, CancellationToken ct)
    {
        var distinct = texts.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = "" };
        if (distinct.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT x, public.f_unaccent(lower(x)) FROM unnest(@a) AS x", c);
            cmd.Parameters.AddWithValue("a", distinct);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                map[rd.GetString(0)] = rd.GetString(1);
            }
        }

        return new PgTextFolder(map);
    }

    public string Fold(string s) => _folded.TryGetValue(s, out var f) ? f : throw new InvalidOperationException("Text was not prepared for folding.");
}

/// <summary>A message as the in-memory matcher sees it at delivery time (labels and flags it is about to get).</summary>
public sealed record SearchSubject
{
    public string? Subject { get; init; }
    public MailboxAddress? From { get; init; }
    public IReadOnlyList<MailboxAddress> To { get; init; } = [];
    public IReadOnlyList<MailboxAddress> Cc { get; init; } = [];
    public IReadOnlyList<MailboxAddress> Bcc { get; init; } = [];
    public IReadOnlyList<string> DeliveredTo { get; init; } = [];
    public IReadOnlyList<string> FileNames { get; init; } = [];
    public bool HasAttachments { get; init; }
    public string? ListId { get; init; }
    public string? RfcMessageId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public long Size { get; init; }
    public bool Seen { get; init; }
    public bool Starred { get; init; }
    public bool Important { get; init; }
    public bool Muted { get; init; }
    public IReadOnlyCollection<string> SystemLabels { get; init; } = [];
    public IReadOnlyCollection<string> UserLabels { get; init; } = [];

    /// <summary>Indexed text per field, exactly what goes into search_vector (MailDelivery).</summary>
    public string IndexSubject { get; init; } = "";
    public string IndexPeople { get; init; } = "";
    public string IndexFiles { get; init; } = "";
    public string IndexBody { get; init; } = "";

    public static SearchSubject FromFacts(MessageFacts f, DateTimeOffset receivedAt, long size, IReadOnlyList<string> deliveredTo, IReadOnlyCollection<string> systemLabels) => new()
    {
        Subject = f.Subject,
        From = f.From,
        To = f.To,
        Cc = f.Cc,
        Bcc = f.Bcc,
        DeliveredTo = deliveredTo.Select(d => d.ToLowerInvariant()).ToList(),
        FileNames = f.Attachments.Where(a => !a.Inline && a.FileName is not null).Select(a => a.FileName!).ToList(),
        HasAttachments = f.HasAttachments,
        ListId = f.ListId,
        RfcMessageId = f.RfcMessageId,
        ReceivedAt = receivedAt,
        Size = size,
        SystemLabels = systemLabels,
        IndexSubject = SearchText.Prepare(f.Subject, 4000),
        IndexPeople = SearchText.Prepare(SearchText.People(f), 20_000),
        IndexFiles = SearchText.Prepare(string.Join(' ', f.Attachments.Select(a => a.FileName)), 20_000),
        IndexBody = SearchText.Prepare(f.BodyText),
    };
}

/// <summary>Evaluates a <see cref="SearchNode"/> against one message, with the same meaning as <see cref="SearchSql"/>.</summary>
public sealed class SearchMatcher(ITextFolder fold, IReadOnlyList<string> myAddresses)
{
    private readonly HashSet<string> _me = new(myAddresses.Select(a => a.ToLowerInvariant()), StringComparer.Ordinal);
    private readonly Dictionary<SearchSubject, string[][]> _tokens = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every string the matcher may fold for these queries and this message (for <see cref="PgTextFolder"/>).</summary>
    public static IEnumerable<string> TextsToFold(SearchSubject s, IEnumerable<SearchNode> queries)
    {
        foreach (var t in new[] { s.Subject, s.From?.Address, s.From?.Name, s.ListId, s.IndexSubject, s.IndexPeople, s.IndexFiles, s.IndexBody })
        {
            if (t is not null)
            {
                yield return t;
            }
        }

        foreach (var a in s.To.Concat(s.Cc).Concat(s.Bcc))
        {
            yield return a.Address;
            if (a.Name is not null)
            {
                yield return a.Name;
            }
        }

        foreach (var f in s.FileNames)
        {
            yield return f;
        }

        foreach (var l in s.UserLabels)
        {
            yield return SearchSql.LabelKey(l);
        }

        foreach (var v in queries.SelectMany(Values))
        {
            yield return v;
        }
    }

    private static IEnumerable<string> Values(SearchNode n) => n switch
    {
        AndNode a => a.Items.SelectMany(Values),
        OrNode o => o.Items.SelectMany(Values),
        NotNode x => Values(x.Item),
        TextNode t => [SearchText.Prepare(t.Text)],
        AddressNode a => [a.Value],
        FieldNode f => [f.Value],
        LabelNode l => [SearchSql.LabelKey(l.Name)],
        _ => [],
    };

    public bool Matches(SearchNode node, SearchSubject m) => node switch
    {
        AndNode a => a.Items.All(i => Matches(i, m)),
        OrNode o => o.Items.Any(i => Matches(i, m)),
        NotNode n => !Matches(n.Item, m),
        TextNode t => Text(t, m),
        AddressNode a => Address(a, m),
        FieldNode { Field: TextField.Subject } f => Contains(m.Subject, f.Value),
        FieldNode { Field: TextField.List } f => Contains(m.ListId, f.Value),
        FieldNode { Field: TextField.FileName } f => m.FileNames.Any(n => Contains(n, f.Value)),
        MessageIdNode id => string.Equals(m.RfcMessageId, id.MessageId, StringComparison.Ordinal),
        LabelNode l => m.UserLabels.Any(u => fold.Fold(SearchSql.LabelKey(u)) == fold.Fold(SearchSql.LabelKey(l.Name)))
                       || m.SystemLabels.Any(k => k.Equals(l.Name, StringComparison.OrdinalIgnoreCase)),
        InNode { Where: InNode.Anywhere } => true,
        InNode i => m.SystemLabels.Contains(i.Where),
        IsNode { State: MessageState.Unread } => !m.Seen,
        IsNode { State: MessageState.Read } => m.Seen,
        IsNode { State: MessageState.Starred } => m.Starred,
        IsNode { State: MessageState.Important } => m.Important,
        IsNode { State: MessageState.Snoozed } => m.SystemLabels.Contains("SNOOZED"),
        IsNode { State: MessageState.Muted } => m.Muted,
        HasNode { What: HasWhat.Attachment } => m.HasAttachments,
        HasNode { What: HasWhat.UserLabels } => m.UserLabels.Count > 0,
        HasNode { What: HasWhat.NoUserLabels } => m.UserLabels.Count == 0,
        DateNode d => d.After ? m.ReceivedAt >= d.At : m.ReceivedAt < d.At,
        SizeNode s => s.Larger ? m.Size > s.Bytes : m.Size < s.Bytes,
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    private bool Contains(string? field, string value) => fold.Fold(field ?? "").Contains(fold.Fold(value), StringComparison.Ordinal);

    private bool Address(AddressNode a, SearchSubject m)
    {
        if (a.Field == AddressField.DeliveredTo)
        {
            return a.Me ? m.DeliveredTo.Any(_me.Contains) : m.DeliveredTo.Contains(a.Value.ToLowerInvariant());
        }

        IEnumerable<MailboxAddress> list = a.Field switch
        {
            AddressField.From => m.From is null ? [] : [m.From],
            AddressField.To => m.To,
            AddressField.Cc => m.Cc,
            _ => m.Bcc,
        };
        return a.Me
            ? list.Any(x => _me.Contains(x.Address.ToLowerInvariant()))
            : list.Any(x => Contains(x.Address, a.Value) || (x.Name is not null && Contains(x.Name, a.Value)));
    }

    /// <summary>Words: all present in some field; phrase: consecutive inside one field (vectors are concatenated, not merged).</summary>
    private bool Text(TextNode t, SearchSubject m)
    {
        var q = Split(fold.Fold(SearchText.Prepare(t.Text)));
        if (q.Length == 0)
        {
            return true;
        }

        if (!_tokens.TryGetValue(m, out var fields))
        {
            fields = [Split(fold.Fold(m.IndexSubject)), Split(fold.Fold(m.IndexPeople)), Split(fold.Fold(m.IndexFiles)), Split(fold.Fold(m.IndexBody))];
            _tokens[m] = fields;
        }

        if (!t.Phrase)
        {
            return q.All(w => fields.Any(f => f.Contains(w)));
        }

        return fields.Any(f => IndexOf(f, q) >= 0);
    }

    private static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int IndexOf(string[] hay, string[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length && ok; j++)
            {
                ok = hay[i + j] == needle[j];
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }
}
