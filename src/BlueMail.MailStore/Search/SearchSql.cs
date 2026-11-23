using Npgsql;

namespace BlueMail.MailStore.Search;

/// <summary>
/// Translates a <see cref="SearchNode"/> into a WHERE fragment over <c>mail.messages m</c>. Every value is a
/// parameter; no user text is ever spliced into SQL. Text folding (lower + unaccent) happens in PostgreSQL on both
/// sides, exactly as <see cref="PgTextFolder"/> does for the in-memory matcher.
/// </summary>
public sealed class SearchSql
{
    private readonly List<NpgsqlParameter> _params = [];
    private readonly IReadOnlyList<string> _myAddresses;
    private int _n;

    private SearchSql(IReadOnlyList<string> myAddresses) => _myAddresses = myAddresses.Select(a => a.ToLowerInvariant()).ToArray();

    public static (string Sql, IReadOnlyList<NpgsqlParameter> Parameters) Translate(SearchNode node, IReadOnlyList<string> myAddresses)
    {
        var t = new SearchSql(myAddresses);
        var sql = t.Node(node);
        return (sql, t._params);
    }

    private string P(object value)
    {
        var name = "s" + _n++;
        _params.Add(new NpgsqlParameter(name, value));
        return "@" + name;
    }

    private string Contains(string column, string value) =>
        $"public.f_unaccent(lower(coalesce({column}, ''))) LIKE '%' || public.f_unaccent(lower({P(EscapeLike(value))})) || '%'";

    public static string EscapeLike(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string SystemLabel(string key) =>
        $"EXISTS (SELECT 1 FROM mail.message_labels ml JOIN mail.labels l ON l.id = ml.label_id WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND l.system_key = '{key}')";

    private string Node(SearchNode node) => node switch
    {
        AndNode a => "(" + string.Join(" AND ", a.Items.Select(Node)) + ")",
        OrNode o => "(" + string.Join(" OR ", o.Items.Select(Node)) + ")",
        NotNode n => "NOT " + Node(n.Item),
        TextNode t => Text(t),
        AddressNode a => Address(a),
        FieldNode { Field: TextField.Subject } f => Contains("m.subject", f.Value),
        FieldNode { Field: TextField.List } f => Contains("m.list_id", f.Value),
        FieldNode { Field: TextField.FileName } f =>
            $"EXISTS (SELECT 1 FROM mail.attachments a WHERE a.tenant_id = m.tenant_id AND a.message_id = m.id AND NOT a.is_inline AND a.deleted_at IS NULL AND {Contains("a.file_name", f.Value)})",
        MessageIdNode id => $"m.rfc_message_id = {P(id.MessageId)}",
        LabelNode l => $"""
            EXISTS (SELECT 1 FROM mail.message_labels ml JOIN mail.labels l ON l.id = ml.label_id
                    WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND l.deleted_at IS NULL
                      AND (public.f_unaccent(translate(lower(l.name), ' /', '--')) = public.f_unaccent({P(LabelKey(l.Name))}) OR lower(l.system_key) = {P(l.Name.ToLowerInvariant())}))
            """,
        InNode { Where: InNode.Anywhere } => "true",
        InNode i => SystemLabel(i.Where),
        IsNode { State: MessageState.Unread } => "NOT m.seen",
        IsNode { State: MessageState.Read } => "m.seen",
        IsNode { State: MessageState.Starred } => "m.flagged",
        IsNode { State: MessageState.Important } => "m.is_important",
        IsNode { State: MessageState.Snoozed } => SystemLabel("SNOOZED"),
        IsNode { State: MessageState.Muted } => "EXISTS (SELECT 1 FROM mail.threads th WHERE th.id = m.thread_id AND th.muted)",
        HasNode { What: HasWhat.Attachment } => "m.has_attachments",
        HasNode { What: HasWhat.UserLabels } => UserLabels(),
        HasNode { What: HasWhat.NoUserLabels } => "NOT " + UserLabels(),
        DateNode d => $"m.received_at {(d.After ? ">=" : "<")} {P(d.At.ToUniversalTime())}",
        SizeNode s => $"m.size {(s.Larger ? ">" : "<")} {P(s.Bytes)}",
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    private static string UserLabels() =>
        "EXISTS (SELECT 1 FROM mail.message_labels ml JOIN mail.labels l ON l.id = ml.label_id WHERE ml.tenant_id = m.tenant_id AND ml.message_id = m.id AND l.system_key IS NULL AND l.deleted_at IS NULL)";

    /// <summary>Gmail's label form: lower case, spaces and "/" as "-" (compared accent-insensitively).</summary>
    public static string LabelKey(string name) => name.Trim().ToLowerInvariant().Replace(' ', '-').Replace('/', '-');

    private string Text(TextNode t)
    {
        var prepared = SearchText.Prepare(t.Text);
        if (string.IsNullOrWhiteSpace(prepared))
        {
            return "true"; // only punctuation: matches like Gmail (nothing to look for)
        }

        var fn = t.Phrase ? "phraseto_tsquery" : "plainto_tsquery";
        return $"m.search_vector @@ {fn}('simple', public.f_unaccent({P(prepared)}))";
    }

    private string Address(AddressNode a)
    {
        if (a.Field == AddressField.DeliveredTo)
        {
            return a.Me ? $"m.delivered_to && {P(_myAddresses.ToArray())}" : $"{P(a.Value.ToLowerInvariant())} = ANY(m.delivered_to)";
        }

        if (a.Field == AddressField.From)
        {
            return a.Me
                ? $"lower(m.from_addr) = ANY({P(_myAddresses.ToArray())})"
                : $"({Contains("m.from_addr", a.Value)} OR {Contains("m.from_name", a.Value)})";
        }

        var column = a.Field switch { AddressField.To => "m.to_addrs", AddressField.Cc => "m.cc_addrs", _ => "m.bcc_addrs" };
        var cond = a.Me
            ? $"lower(e->>'address') = ANY({P(_myAddresses.ToArray())})"
            : $"{Contains("e->>'address'", a.Value)} OR {Contains("e->>'name'", a.Value)}";
        return $"EXISTS (SELECT 1 FROM jsonb_array_elements({column}) e WHERE {cond})";
    }
}
