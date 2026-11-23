using System.Globalization;
using System.Text;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;

namespace BlueMail.Imap;

internal sealed partial class ImapSession
{
    private abstract record Crit;
    private sealed record And(List<Crit> Items) : Crit;
    private sealed record Or(Crit A, Crit B) : Crit;
    private sealed record Not(Crit A) : Crit;
    private sealed record Flag(string Name, bool Value) : Crit;
    private sealed record Keyword(string Name, bool Value) : Crit;
    private sealed record Seq(ImapSequenceSet Set, bool Uid) : Crit;
    private sealed record Size(long Bytes, bool Larger) : Crit;
    private sealed record Date(DateOnly Day, string Op, bool Sent) : Crit;
    private sealed record Field(string Name, string Text) : Crit;
    private sealed record Text(string Value, bool BodyOnly) : Crit;
    private sealed record Modseq(long Value) : Crit;
    private sealed record All : Crit;
    private sealed record RecentCrit : Crit;

    private async Task SearchAsync(string tag, bool uid, ImapParser p, CancellationToken ct)
    {
        p.SkipSpaces();
        var values = p.ReadAll();
        var i = 0;
        if (values.Count > 1 && (values[0].AsText() ?? "").Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
        {
            var cs = (values[1].AsText() ?? "").ToUpperInvariant();
            if (cs is not ("UTF-8" or "US-ASCII"))
            {
                throw new ImapNoException("Charset not supported", "BADCHARSET (UTF-8 US-ASCII)");
            }

            i = 2;
        }

        var items = new List<Crit>();
        while (i < values.Count)
        {
            items.Add(ParseCrit(values, ref i));
        }

        if (items.Count == 0)
        {
            throw new ImapBadException("Missing search criteria");
        }

        var root = new And(items);
        var rows = _sel!.Rows;
        var ctx = new SearchContext(this, rows, ct);
        var hits = new List<(int Seq, ImapMessageRow Row)>();
        for (var n = 0; n < rows.Count; n++)
        {
            if (await ctx.EvalAsync(root, n))
            {
                hits.Add((n + 1, rows[n]));
            }
        }

        var numbers = hits.Select(h => uid ? h.Row.Uid : h.Seq).ToList();
        var modseq = ContainsModseq(root) && hits.Count > 0 ? $" (MODSEQ {hits.Max(h => h.Row.Modseq)})" : "";
        _w.Line("* SEARCH" + string.Concat(numbers.Select(x => " " + x.ToString(CultureInfo.InvariantCulture))) + modseq);
        await RefreshAsync(allowExpunge: uid, ct);
        Ok(tag, (uid ? "UID " : "") + "SEARCH completed");
    }

    private static bool ContainsModseq(Crit c) => c switch
    {
        Modseq => true,
        And a => a.Items.Any(ContainsModseq),
        Or o => ContainsModseq(o.A) || ContainsModseq(o.B),
        Not n => ContainsModseq(n.A),
        _ => false,
    };

    private Crit ParseCrit(List<ImapValue> v, ref int i)
    {
        if (i >= v.Count)
        {
            throw new ImapBadException("Incomplete search");
        }

        var x = v[i++];
        if (x is ImapList list)
        {
            var inner = new List<Crit>();
            var j = 0;
            var items = list.Items.ToList();
            while (j < items.Count)
            {
                inner.Add(ParseCrit(items, ref j));
            }

            return new And(inner);
        }

        var key = (x.AsText() ?? throw new ImapBadException("Invalid search key")).ToUpperInvariant();
        string Arg(ref int k) => k < v.Count
            ? v[k++] switch { ImapNil => "NIL", ImapList => throw new ImapBadException("Invalid argument"), var a => a.AsText() ?? throw new ImapBadException("Argument too large") }
            : throw new ImapBadException("Missing argument");
        DateOnly Day(ref int k)
        {
            var t = Arg(ref k);
            return DateOnly.TryParseExact(t, ["d-MMM-yyyy", "dd-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ImapBadException("Invalid date");
        }

        switch (key)
        {
            case "ALL": return new All();
            case "ANSWERED": return new Flag("\\Answered", true);
            case "UNANSWERED": return new Flag("\\Answered", false);
            case "DELETED": return new Flag("\\Deleted", true);
            case "UNDELETED": return new Flag("\\Deleted", false);
            case "DRAFT": return new Flag("\\Draft", true);
            case "UNDRAFT": return new Flag("\\Draft", false);
            case "FLAGGED": return new Flag("\\Flagged", true);
            case "UNFLAGGED": return new Flag("\\Flagged", false);
            case "SEEN": return new Flag("\\Seen", true);
            case "OLD": return new Not(new RecentCrit());
            case "UNSEEN": return new Flag("\\Seen", false);
            case "NEW": return new And([new RecentCrit(), new Flag("\\Seen", false)]);
            case "RECENT": return new RecentCrit();
            case "KEYWORD": return new Keyword(Arg(ref i), true);
            case "UNKEYWORD": return new Keyword(Arg(ref i), false);
            case "LARGER": return new Size(long.Parse(Arg(ref i), NumberStyles.None, CultureInfo.InvariantCulture), true);
            case "SMALLER": return new Size(long.Parse(Arg(ref i), NumberStyles.None, CultureInfo.InvariantCulture), false);
            case "BEFORE" or "ON" or "SINCE": return new Date(Day(ref i), key, false);
            case "SENTBEFORE" or "SENTON" or "SENTSINCE": return new Date(Day(ref i), key[4..], true);
            case "FROM" or "TO" or "CC" or "BCC" or "SUBJECT": return new Field(key, Arg(ref i));
            case "HEADER": { var name = Arg(ref i); return new Field("HEADER:" + name, Arg(ref i)); }
            case "BODY": return new Text(Arg(ref i), true);
            case "TEXT": return new Text(Arg(ref i), false);
            case "UID": return ImapSequenceSet.TryParse(Arg(ref i), out var us) ? new Seq(us, true) : throw new ImapBadException("Invalid UID set");
            case "NOT": return new Not(ParseCrit(v, ref i));
            case "OR": { var a = ParseCrit(v, ref i); return new Or(a, ParseCrit(v, ref i)); }
            case "MODSEQ":
                {
                    // MODSEQ ["entry" type] value (RFC 7162 3.1.5): entry names are accepted and ignored.
                    var first = Arg(ref i);
                    if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                    {
                        _ = Arg(ref i);
                        ms = long.Parse(Arg(ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    }

                    _condstore = true;
                    return new Modseq(ms);
                }

            default:
                return ImapSequenceSet.TryParse(key, out var set) ? new Seq(set, false) : throw new ImapBadException("Unknown search key " + key);
        }
    }

    /// <summary>
    /// Evaluates criteria. In folders up to <see cref="ExactLimit"/> messages, text criteria read the stored messages and
    /// match substrings of decoded header fields and text parts (RFC 3501 6.4.4), and SENT* use the Date header in its own
    /// time zone. Larger folders use the indexed columns and the word index (docs/00 D24).
    /// </summary>
    private sealed class SearchContext(ImapSession s, List<ImapMessageRow> rows, CancellationToken ct)
    {
        public const int ExactLimit = 5000;
        private readonly bool _exact = rows.Count <= ExactLimit;
        private IReadOnlyDictionary<Guid, ImapSearchMeta>? _meta;
        private readonly Dictionary<string, IReadOnlySet<Guid>> _text = [];
        private readonly Dictionary<Guid, (MimeMessage Message, byte[] Raw, string? Body)> _messages = [];

        public async Task<bool> EvalAsync(Crit c, int n)
        {
            var r = rows[n];
            switch (c)
            {
                case All: return true;
                case RecentCrit: return s._sel!.Recent.Contains(r.Uid);
                case And a:
                    foreach (var x in a.Items)
                    {
                        if (!await EvalAsync(x, n))
                        {
                            return false;
                        }
                    }

                    return true;
                case Or o: return await EvalAsync(o.A, n) || await EvalAsync(o.B, n);
                case Not x: return !await EvalAsync(x.A, n);
                case Flag f:
                    return f.Name switch
                    {
                        "\\Answered" => r.Answered,
                        "\\Deleted" => r.Deleted,
                        "\\Draft" => r.Draft,
                        "\\Flagged" => r.Flagged,
                        _ => r.Seen,
                    } == f.Value;
                case Keyword k:
                    var hasKey = k.Name.Equals("$Forwarded", StringComparison.OrdinalIgnoreCase) ? r.Forwarded : r.Keywords.Contains(k.Name, StringComparer.OrdinalIgnoreCase);
                    return hasKey == k.Value;
                case Seq q: return q.Uid ? q.Set.Contains(r.Uid, rows[^1].Uid) : q.Set.Contains(n + 1, rows.Count);
                case Size z: return z.Larger ? r.Size > z.Bytes : r.Size < z.Bytes;
                case Modseq m: return r.Modseq >= m.Value;
                case Date d:
                    {
                        DateOnly? day;
                        if (!d.Sent)
                        {
                            day = DateOnly.FromDateTime(r.ReceivedAt.UtcDateTime);
                        }
                        else if (_exact)
                        {
                            var sent = (await MessageAsync(r.MessageId)).Message.Date;
                            day = sent is { } dt ? DateOnly.FromDateTime(dt.DateTime) : null; // the date as written, in its own zone
                        }
                        else
                        {
                            var sent = (await MetaAsync()).GetValueOrDefault(r.MessageId)?.SentAt;
                            day = sent is { } dt ? DateOnly.FromDateTime(dt.UtcDateTime) : null;
                        }

                        if (day is not { } dd)
                        {
                            return false;
                        }

                        return d.Op switch { "BEFORE" => dd < d.Day, "ON" => dd == d.Day, _ => dd >= d.Day };
                    }

                case Field f when f.Name.StartsWith("HEADER:", StringComparison.Ordinal):
                    {
                        var m = (await MessageAsync(r.MessageId)).Message;
                        return m.Headers.All(f.Name[7..]).Any(x => f.Text.Length == 0 || Has(x, f.Text));
                    }

                case Field f when _exact:
                    {
                        var m = (await MessageAsync(r.MessageId)).Message;
                        var name = f.Name switch { "FROM" => "From", "TO" => "To", "CC" => "Cc", "BCC" => "Bcc", _ => "Subject" };
                        return m.Headers.All(name).Any(x => Has(x, f.Text));
                    }

                case Field f:
                    {
                        var m = (await MetaAsync()).GetValueOrDefault(r.MessageId);
                        var value = m is null ? "" : f.Name switch
                        {
                            "FROM" => m.From,
                            "TO" => m.To,
                            "CC" => m.Cc,
                            "BCC" => m.Bcc,
                            _ => m.Subject,
                        };
                        return value.Contains(f.Text, StringComparison.OrdinalIgnoreCase);
                    }

                case Text t when _exact:
                    {
                        var (m, _, body) = await MessageAsync(r.MessageId);
                        if (body!.Contains(t.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        return !t.BodyOnly && m.Headers.Any(h => Has(h, t.Value) || h.Name.Contains(t.Value, StringComparison.OrdinalIgnoreCase));
                    }

                case Text t:
                    {
                        var key = (t.BodyOnly ? "B:" : "T:") + t.Value;
                        if (!_text.TryGetValue(key, out var set))
                        {
                            set = await s.WithStoreAsync(st => st.ImapTextMatchAsync(s._access!, rows.Select(x => x.MessageId).ToList(), t.Value, ct));
                            _text[key] = set;
                        }

                        if (set.Contains(r.MessageId))
                        {
                            return true;
                        }

                        if (t.BodyOnly)
                        {
                            return false;
                        }

                        var m = (await MetaAsync()).GetValueOrDefault(r.MessageId);
                        return m is not null && (m.From + m.To + m.Cc + m.Subject).Contains(t.Value, StringComparison.OrdinalIgnoreCase);
                    }

                default:
                    return false;
            }
        }

        /// <summary>A header field contains the text: decoded (encoded words) or as written.</summary>
        private static bool Has(HeaderField h, string text) =>
            h.Text().Contains(text, StringComparison.OrdinalIgnoreCase) || h.Value().Contains(text, StringComparison.OrdinalIgnoreCase)
            || (h.Name.ToUpperInvariant() is "FROM" or "TO" or "CC" or "BCC" or "SENDER" or "REPLY-TO"
                && AddressParser.Parse(h.Value()).Any(a => a.Address.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                          || (a.Name ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)));

        private async Task<IReadOnlyDictionary<Guid, ImapSearchMeta>> MetaAsync() =>
            _meta ??= await s.WithStoreAsync(st => st.ImapMetaAsync(s._access!, rows.Select(x => x.MessageId).ToList(), ct));

        private async Task<(MimeMessage Message, byte[] Raw, string? Body)> MessageAsync(Guid id)
        {
            if (!_messages.TryGetValue(id, out var e))
            {
                using var ms = new MemoryStream();
                byte[] raw;
                try
                {
                    await s.WithStoreAsync(st => st.CopyRawAsync(s._access!, id, ms, ct));
                    raw = ms.ToArray();
                }
                catch (Exception x) when (IsStorageFailure(x))
                {
                    // Content lost in storage (BUG-078): search what the database keeps of it, as for large folders.
                    s.Log.LogError(x, "IMAP {User}: content of message {MessageId} could not be read", s._login?.Email ?? "-", id);
                    var meta = (await MetaAsync()).GetValueOrDefault(id);
                    raw = Encoding.UTF8.GetBytes(meta is null ? "\r\n" :
                        $"From: {meta.From}\r\nTo: {meta.To}\r\nCc: {meta.Cc}\r\nBcc: {meta.Bcc}\r\nSubject: {meta.Subject}\r\n"
                        + (meta.SentAt is { } sent ? "Date: " + sent.ToString("r", CultureInfo.InvariantCulture) + "\r\n" : "") + "\r\n");
                }

                var m = MimeMessage.Parse(raw);
                var source = new MemoryStream(raw, writable: false);
                var body = new StringBuilder();
                foreach (var part in m.Root.Walk().Where(p => p.IsLeaf))
                {
                    if (part.MediaType.StartsWith("text/", StringComparison.Ordinal))
                    {
                        body.Append(part.ReadText(source)).Append('\n');
                    }
                }

                e = (m, raw, body.ToString());
                _messages[id] = e;
            }

            return e;
        }
    }
}
