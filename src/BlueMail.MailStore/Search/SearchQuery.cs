using System.Globalization;
using System.Text;

namespace BlueMail.MailStore.Search;

/// <summary>
/// The parsed form of a Gmail-style query (PROMPT 6.7). Search (SQL) and filters (in memory) evaluate the same
/// tree, so a filter and the search that created it always agree (PROMPT 6.8).
/// </summary>
public abstract record SearchNode;

public sealed record AndNode(IReadOnlyList<SearchNode> Items) : SearchNode;

public sealed record OrNode(IReadOnlyList<SearchNode> Items) : SearchNode;

public sealed record NotNode(SearchNode Item) : SearchNode;

/// <summary>Free text over subject, people, file names and body (word match, accent-insensitive).</summary>
public sealed record TextNode(string Text, bool Phrase) : SearchNode;

public enum AddressField { From, To, Cc, Bcc, DeliveredTo }

/// <summary>Substring of an address or display name; <see cref="Me"/> = one of the searcher's own addresses.</summary>
public sealed record AddressNode(AddressField Field, string Value, bool Me) : SearchNode;

public enum TextField { Subject, FileName, List }

/// <summary>Substring of the subject, an attachment name or the List-Id (accent-insensitive).</summary>
public sealed record FieldNode(TextField Field, string Value) : SearchNode;

public sealed record MessageIdNode(string MessageId) : SearchNode;

/// <summary>A user label by name (Gmail form: case-insensitive, spaces and slashes as dashes) or a system label.</summary>
public sealed record LabelNode(string Name) : SearchNode;

/// <summary>in: a system label key, or "ANYWHERE" (no Spam/Trash exclusion).</summary>
public sealed record InNode(string Where) : SearchNode
{
    public const string Anywhere = "ANYWHERE";
}

public enum MessageState { Unread, Read, Starred, Important, Snoozed, Muted }

public sealed record IsNode(MessageState State) : SearchNode;

public enum HasWhat { Attachment, UserLabels, NoUserLabels }

public sealed record HasNode(HasWhat What) : SearchNode;

/// <summary>Received at or after (<see cref="After"/> true) or before an instant.</summary>
public sealed record DateNode(bool After, DateTimeOffset At) : SearchNode;

public sealed record SizeNode(bool Larger, long Bytes) : SearchNode;

/// <summary>A query that cannot be understood: the code is translated for the user (search.error.&lt;code&gt;).</summary>
public sealed class SearchSyntaxException(string code, int position, string? detail = null)
    : FormatException($"{code} at {position}" + (detail is null ? "" : ": " + detail))
{
    public string Code { get; } = code;
    public int Position { get; } = position;
    public string? Detail { get; } = detail;
}

/// <summary>What a query is evaluated against: the user's time zone (dates are local days) and "now".</summary>
public sealed record SearchContext(TimeZoneInfo Zone, DateTimeOffset Now)
{
    public static SearchContext Utc() => new(TimeZoneInfo.Utc, DateTimeOffset.UtcNow);
}

/// <summary>Recursive-descent parser for the Gmail operator language. Never treats a broken query as plain words.</summary>
public sealed class SearchParser
{
    public const int MaxLength = 2000;

    /// <summary>Every <see cref="SearchSyntaxException.Code"/> the parser can raise (each needs a translation).</summary>
    public static readonly IReadOnlyList<string> ErrorCodes =
    [
        "query_too_long", "unbalanced_parenthesis", "unbalanced_brace", "unexpected_token", "unterminated_quote", "too_deep", "dangling_or",
        "dangling_and", "empty_group", "nothing_to_negate", "unexpected_end", "operator_inside_operator", "missing_value", "empty_phrase",
        "invalid_value", "unknown_operator", "invalid_date", "invalid_period", "invalid_size", "date_range_reversed",
    ];
    public const int MaxDepth = 20;

    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "to", "cc", "bcc", "subject", "label", "in", "is", "has", "filename", "category", "after", "before", "older", "newer",
        "older_than", "newer_than", "larger", "smaller", "size", "list", "deliveredto", "rfc822msgid",
    };

    private static readonly Dictionary<string, string> InValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inbox"] = "INBOX", ["sent"] = "SENT", ["drafts"] = "DRAFTS", ["draft"] = "DRAFTS", ["spam"] = "SPAM", ["trash"] = "TRASH",
        ["starred"] = "STARRED", ["important"] = "IMPORTANT", ["snoozed"] = "SNOOZED", ["scheduled"] = "SCHEDULED", ["anywhere"] = InNode.Anywhere,
    };

    private static readonly Dictionary<string, MessageState> IsValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unread"] = MessageState.Unread, ["read"] = MessageState.Read, ["starred"] = MessageState.Starred, ["important"] = MessageState.Important,
        ["snoozed"] = MessageState.Snoozed, ["muted"] = MessageState.Muted,
    };

    private static readonly Dictionary<string, HasWhat> HasValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["attachment"] = HasWhat.Attachment, ["userlabels"] = HasWhat.UserLabels, ["nouserlabels"] = HasWhat.NoUserLabels,
    };

    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["primary"] = "CATEGORY_PRIMARY", ["social"] = "CATEGORY_SOCIAL", ["promotions"] = "CATEGORY_PROMOTIONS", ["updates"] = "CATEGORY_UPDATES",
        ["forums"] = "CATEGORY_FORUMS",
    };

    private enum Kind { Word, Quoted, LParen, RParen, LBrace, RBrace, Minus, End }

    private readonly record struct Token(Kind Kind, string Text, int Pos);

    private readonly List<Token> _tokens;
    private readonly SearchContext _ctx;
    private int _i;
    private int _depth;

    private SearchParser(List<Token> tokens, SearchContext ctx)
    {
        _tokens = tokens;
        _ctx = ctx;
    }

    /// <summary>Parses a query; null for an empty one. Throws <see cref="SearchSyntaxException"/> for a broken one.</summary>
    public static SearchNode? Parse(string? query, SearchContext ctx)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        if (query.Length > MaxLength)
        {
            throw new SearchSyntaxException("query_too_long", MaxLength);
        }

        var p = new SearchParser(Lex(query), ctx);
        var node = p.ParseOr(null);
        var t = p.Peek();
        if (t.Kind != Kind.End)
        {
            throw t.Kind switch
            {
                Kind.RParen => new SearchSyntaxException("unbalanced_parenthesis", t.Pos),
                Kind.RBrace => new SearchSyntaxException("unbalanced_brace", t.Pos),
                _ => new SearchSyntaxException("unexpected_token", t.Pos, t.Text),
            };
        }

        CheckDateRanges(node);
        return node;
    }

    // ---------- lexer ----------

    private static List<Token> Lex(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '(':
                    list.Add(new Token(Kind.LParen, "(", i++));
                    continue;
                case ')':
                    list.Add(new Token(Kind.RParen, ")", i++));
                    continue;
                case '{':
                    list.Add(new Token(Kind.LBrace, "{", i++));
                    continue;
                case '}':
                    list.Add(new Token(Kind.RBrace, "}", i++));
                    continue;
                case '"':
                {
                    var end = s.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        throw new SearchSyntaxException("unterminated_quote", i);
                    }

                    list.Add(new Token(Kind.Quoted, s[(i + 1)..end], i));
                    i = end + 1;
                    continue;
                }

                case '-' when i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && (list.Count == 0 || IsBoundary(s[i - 1])):
                    list.Add(new Token(Kind.Minus, "-", i++));
                    continue;
            }

            var start = i;
            var sb = new StringBuilder();
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not '(' and not ')' and not '{' and not '}' and not '"')
            {
                sb.Append(s[i++]);
            }

            // "subject:" directly followed by a quote or a group belongs to that operator: keep the word as "subject:".
            list.Add(new Token(Kind.Word, sb.ToString(), start));
        }

        list.Add(new Token(Kind.End, "", s.Length));
        return list;
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is '(' or '{';

    // ---------- grammar ----------

    private Token Peek() => _tokens[_i];

    private Token Next() => _tokens[_i++];

    private void Enter(int pos)
    {
        if (++_depth > MaxDepth)
        {
            throw new SearchSyntaxException("too_deep", pos);
        }
    }

    // or := and ( "OR" and )*
    private SearchNode ParseOr(string? field)
    {
        var items = new List<SearchNode> { ParseAnd(field) };
        while (Peek() is { Kind: Kind.Word, Text: "OR" or "|" })
        {
            var orTok = Next();
            if (Peek().Kind is Kind.End or Kind.RParen or Kind.RBrace || Peek() is { Kind: Kind.Word, Text: "OR" })
            {
                throw new SearchSyntaxException("dangling_or", orTok.Pos);
            }

            items.Add(ParseAnd(field));
        }

        return items.Count == 1 ? items[0] : new OrNode(items);
    }

    // and := unary+ ("AND" is optional)
    private SearchNode ParseAnd(string? field)
    {
        var items = new List<SearchNode>();
        while (true)
        {
            var t = Peek();
            if (t.Kind is Kind.End or Kind.RParen or Kind.RBrace || t is { Kind: Kind.Word, Text: "OR" or "|" })
            {
                break;
            }

            if (t is { Kind: Kind.Word, Text: "AND" })
            {
                Next();
                if (items.Count == 0 || Peek().Kind is Kind.End or Kind.RParen or Kind.RBrace)
                {
                    throw new SearchSyntaxException("dangling_and", t.Pos);
                }

                continue;
            }

            items.Add(ParseUnary(field));
        }

        if (items.Count == 0)
        {
            var t = Peek();
            throw t.Kind == Kind.Word ? new SearchSyntaxException("dangling_or", t.Pos) : new SearchSyntaxException("empty_group", t.Pos);
        }

        return items.Count == 1 ? items[0] : new AndNode(items);
    }

    private SearchNode ParseUnary(string? field)
    {
        var t = Peek();
        if (t.Kind == Kind.Minus)
        {
            Next();
            if (Peek().Kind is Kind.End or Kind.RParen or Kind.RBrace)
            {
                throw new SearchSyntaxException("nothing_to_negate", t.Pos);
            }

            return new NotNode(ParseUnary(field));
        }

        return ParsePrimary(field);
    }

    private SearchNode ParsePrimary(string? field)
    {
        var t = Next();
        switch (t.Kind)
        {
            case Kind.LParen:
            {
                Enter(t.Pos);
                var inner = ParseOr(field);
                if (Next().Kind != Kind.RParen)
                {
                    throw new SearchSyntaxException("unbalanced_parenthesis", t.Pos);
                }

                _depth--;
                return inner;
            }

            case Kind.LBrace:
            {
                // {a b c} = a OR b OR c
                Enter(t.Pos);
                var items = new List<SearchNode>();
                while (Peek().Kind is not (Kind.RBrace or Kind.End))
                {
                    items.Add(ParseUnary(field));
                }

                if (Next().Kind != Kind.RBrace)
                {
                    throw new SearchSyntaxException("unbalanced_brace", t.Pos);
                }

                if (items.Count == 0)
                {
                    throw new SearchSyntaxException("empty_group", t.Pos);
                }

                _depth--;
                return items.Count == 1 ? items[0] : new OrNode(items);
            }

            case Kind.Quoted:
                return field is null ? Text(t.Text, phrase: true, t.Pos) : Operator(field, t.Text, t.Pos);
            case Kind.Word:
                return Word(t, field);
            case Kind.RParen:
                throw new SearchSyntaxException("unbalanced_parenthesis", t.Pos);
            case Kind.RBrace:
                throw new SearchSyntaxException("unbalanced_brace", t.Pos);
            default:
                throw new SearchSyntaxException("unexpected_end", t.Pos);
        }
    }

    private SearchNode Word(Token t, string? field)
    {
        var colon = t.Text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && Operators.Contains(t.Text[..colon]))
        {
            if (field is not null)
            {
                throw new SearchSyntaxException("operator_inside_operator", t.Pos, t.Text[..colon]);
            }

            var op = t.Text[..colon].ToLowerInvariant();
            var value = t.Text[(colon + 1)..];
            if (value.Length > 0)
            {
                return Operator(op, value, t.Pos);
            }

            // "subject:" followed by "quoted text" or a (group)
            var next = Peek();
            if (next.Kind == Kind.Quoted && next.Pos == t.Pos + t.Text.Length)
            {
                Next();
                return Operator(op, next.Text, t.Pos);
            }

            if (next.Kind is Kind.LParen or Kind.LBrace && next.Pos == t.Pos + t.Text.Length)
            {
                return ParsePrimary(op);
            }

            throw new SearchSyntaxException("missing_value", t.Pos, op);
        }

        return field is null ? Text(t.Text, phrase: false, t.Pos) : Operator(field, t.Text, t.Pos);
    }

    private static TextNode Text(string text, bool phrase, int pos)
    {
        if (phrase && text.Trim().Length == 0)
        {
            throw new SearchSyntaxException("empty_phrase", pos);
        }

        return new TextNode(text, phrase);
    }

    private SearchNode Operator(string op, string value, int pos)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            throw new SearchSyntaxException("missing_value", pos, op);
        }

        switch (op)
        {
            case "from": return Address(AddressField.From, value);
            case "to": return Address(AddressField.To, value);
            case "cc": return Address(AddressField.Cc, value);
            case "bcc": return Address(AddressField.Bcc, value);
            case "deliveredto": return Address(AddressField.DeliveredTo, value);
            case "subject": return new FieldNode(TextField.Subject, value);
            case "filename": return new FieldNode(TextField.FileName, value);
            case "list": return new FieldNode(TextField.List, value);
            case "rfc822msgid": return new MessageIdNode(value.Trim('<', '>'));
            case "label": return new LabelNode(value);
            case "in":
                return InValues.TryGetValue(value, out var where) ? new InNode(where) : new LabelNode(value); // Gmail: in:<label> works too
            case "is":
                return IsValues.TryGetValue(value, out var state) ? new IsNode(state) : throw new SearchSyntaxException("invalid_value", pos, "is:" + value);
            case "has":
                return HasValues.TryGetValue(value, out var what) ? new HasNode(what) : throw new SearchSyntaxException("invalid_value", pos, "has:" + value);
            case "category":
                return Categories.TryGetValue(value, out var cat) ? new InNode(cat) : throw new SearchSyntaxException("invalid_value", pos, "category:" + value);
            case "after" or "newer": return new DateNode(true, Date(value, pos));
            case "before" or "older": return new DateNode(false, Date(value, pos));
            case "newer_than": return new DateNode(true, Period(value, pos));
            case "older_than": return new DateNode(false, Period(value, pos));
            case "larger" or "size": return new SizeNode(true, Size(value, pos));
            case "smaller": return new SizeNode(false, Size(value, pos));
            default: throw new SearchSyntaxException("unknown_operator", pos, op);
        }
    }

    private static AddressNode Address(AddressField f, string value) =>
        value.Equals("me", StringComparison.OrdinalIgnoreCase) ? new AddressNode(f, "", true) : new AddressNode(f, value, false);

    /// <summary>yyyy/mm/dd, yyyy-mm-dd (local midnight in the user's zone) or Unix seconds.</summary>
    private DateTimeOffset Date(string v, int pos)
    {
        if (v.All(char.IsAsciiDigit) && v.Length >= 9 && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(Math.Min(unix, 253402300799));
        }

        var parts = v.Split('/', '-');
        if (parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 4 && p.All(char.IsAsciiDigit)) && parts[0].Length == 4
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var y) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var d) && y is >= 1970 and <= 9999 && m is >= 1 and <= 12 && d >= 1
            && d <= DateTime.DaysInMonth(y, m))
        {
            var local = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, _ctx.Zone.GetUtcOffset(local));
        }

        throw new SearchSyntaxException("invalid_date", pos, v);
    }

    /// <summary>N followed by d (days), m (months) or y (years), relative to now.</summary>
    private DateTimeOffset Period(string v, int pos)
    {
        if (v.Length >= 2 && int.TryParse(v[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 100_000)
        {
            switch (char.ToLowerInvariant(v[^1]))
            {
                case 'd': return _ctx.Now.AddDays(-n);
                case 'm' when n <= 12_000: return _ctx.Now.AddMonths(-n);
                case 'y' when n <= 1000: return _ctx.Now.AddYears(-n);
            }
        }

        throw new SearchSyntaxException("invalid_period", pos, v);
    }

    /// <summary>Bytes, or with K / M / G suffix (binary multiples, as Gmail).</summary>
    private static long Size(string v, int pos)
    {
        var mult = 1L;
        var num = v.Length > 1 && v[^1] is 'B' or 'b' && char.IsLetter(v[^2]) ? v[..^1] : v; // "1GB" = "1G"
        switch (char.ToUpperInvariant(num[^1]))
        {
            case 'K': mult = 1L << 10; num = num[..^1]; break;
            case 'M': mult = 1L << 20; num = num[..^1]; break;
            case 'G': mult = 1L << 30; num = num[..^1]; break;
        }

        return long.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= long.MaxValue / mult / 2
            ? n * mult
            : throw new SearchSyntaxException("invalid_size", pos, v);
    }

    /// <summary>"after:2026/03/01 before:2026/02/01" can match nothing: say so instead of returning an empty list.</summary>
    private static void CheckDateRanges(SearchNode node)
    {
        switch (node)
        {
            case AndNode a:
                var dates = a.Items.OfType<DateNode>().ToList();
                var from = dates.Where(d => d.After).Select(d => (DateTimeOffset?)d.At).Max();
                var to = dates.Where(d => !d.After).Select(d => (DateTimeOffset?)d.At).Min();
                if (from is not null && to is not null && from >= to)
                {
                    throw new SearchSyntaxException("date_range_reversed", 0);
                }

                foreach (var i in a.Items)
                {
                    CheckDateRanges(i);
                }

                break;
            case OrNode o:
                foreach (var i in o.Items)
                {
                    CheckDateRanges(i);
                }

                break;
            case NotNode n:
                CheckDateRanges(n.Item);
                break;
        }
    }

    /// <summary>True when the query itself says where to look (in:spam, in:trash, in:anywhere), so Spam/Trash are not hidden.</summary>
    public static bool LooksInSpamOrTrash(SearchNode? node) => node switch
    {
        InNode i => i.Where is InNode.Anywhere or "SPAM" or "TRASH",
        LabelNode l => l.Name.Equals("spam", StringComparison.OrdinalIgnoreCase) || l.Name.Equals("trash", StringComparison.OrdinalIgnoreCase),
        AndNode a => a.Items.Any(LooksInSpamOrTrash),
        OrNode o => o.Items.Any(LooksInSpamOrTrash),
        NotNode => false,
        _ => false,
    };
}
