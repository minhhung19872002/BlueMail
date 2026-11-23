using System.Globalization;
using System.Text;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;

namespace BlueMail.Imap;

/// <summary>
/// FETCH data built from a stored message (RFC 3501 7.4.2): ENVELOPE, BODY / BODYSTRUCTURE and BODY[section] slices.
/// Part numbers follow RFC 3501 6.4.5: children of a multipart are 1..n, a non-multipart body is part 1, and the parts
/// of an attached message are numbered inside it.
/// </summary>
public sealed class ImapMessageFormat
{
    private readonly byte[] raw;
    private readonly MimeMessage _message;

    /// <summary>
    /// Where each part's content ends for IMAP: up to the next boundary of its parent (a nested multipart keeps its
    /// epilogue, the message body runs to the end), which is not always where the MIME parser stopped.
    /// </summary>
    private readonly Dictionary<MimeEntity, long> _ends = new(ReferenceEqualityComparer.Instance);

    public ImapMessageFormat(byte[] raw)
    {
        this.raw = raw;
        _message = MimeMessage.Parse(raw);
        Assign(_message.Root, raw.LongLength);
    }

    private void Assign(MimeEntity e, long end)
    {
        _ends[e] = end;
        if (e.IsMultipart && e.ContentType["boundary"] is { } boundary)
        {
            var delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
            for (var i = 0; i < e.Children.Count; i++)
            {
                long childEnd;
                if (i + 1 < e.Children.Count)
                {
                    var next = e.Children[i + 1].HeaderOffset;
                    var at = raw.AsSpan(0, (int)Math.Min(next, raw.LongLength)).LastIndexOf(delimiter);
                    childEnd = at >= 0 ? at : e.Children[i].BodyEndOffset;
                }
                else
                {
                    var from = (int)Math.Min(e.Children[i].BodyOffset, raw.LongLength);
                    var close = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--");
                    var at = raw.AsSpan(from, (int)(Math.Min(end, raw.LongLength) - from)).IndexOf(close);
                    childEnd = at >= 0 ? from + at : e.Children[i].BodyEndOffset;
                }

                Assign(e.Children[i], childEnd);
            }
        }
        else if (e.IsMessage)
        {
            Assign(e.Children[0], end);
        }
    }

    private long End(MimeEntity e) => _ends.TryGetValue(e, out var v) ? v : e.BodyEndOffset;

    public byte[] Raw => raw;

    // ---- ENVELOPE ----

    public void Envelope(ImapResponseWriter w) => Envelope(w, _message.Root);

    private static void Envelope(ImapResponseWriter w, MimeEntity root)
    {
        string? H(string name) => root.Headers[name] is { } h ? HeaderField.Unfold(Encoding.UTF8.GetString(h.RawValue)).Trim() : null;
        List<Entry> A(string name) => Entries(root.Headers[name]?.Value());
        var from = A("From");
        var sender = A("Sender");
        var replyTo = A("Reply-To");
        w.Raw("(").Str(H("Date")).Raw(" ").Str(H("Subject")).Raw(" ");
        Addresses(w, from).Raw(" ");
        Addresses(w, sender.Count > 0 ? sender : from).Raw(" "); // RFC 3501: defaults to From
        Addresses(w, replyTo.Count > 0 ? replyTo : from).Raw(" ");
        Addresses(w, A("To")).Raw(" ");
        Addresses(w, A("Cc")).Raw(" ");
        Addresses(w, A("Bcc")).Raw(" ");
        w.Str(H("In-Reply-To")).Raw(" ").Str(H("Message-ID")).Raw(")");
    }

    /// <summary>An address list entry for ENVELOPE: a mailbox, or the start / end of an RFC 5322 group.</summary>
    private sealed record Entry(MailboxAddress? Mailbox, string? GroupStart, bool GroupEnd, string? Route = null);

    /// <summary>Splits an address list at top-level commas, keeping group syntax ("name: a, b;") that ENVELOPE must show.</summary>
    private static List<Entry> Entries(string? value)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return list;
        }

        var item = new StringBuilder();
        bool quoted = false, inAngle = false, inGroup = false;
        var comment = 0;
        void Flush()
        {
            var text = item.ToString();
            var route = System.Text.RegularExpressions.Regex.Match(text, @"<\s*(@[^:<>]+):", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            foreach (var m in AddressParser.Parse(route.Success ? text.Remove(route.Groups[1].Index, route.Groups[1].Length + 1) : text))
            {
                list.Add(new Entry(m, null, false, route.Success ? route.Groups[1].Value.Trim() : null));
            }

            item.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted)
            {
                item.Append(c);
                if (c == '\\' && i + 1 < value.Length)
                {
                    item.Append(value[++i]);
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when comment == 0:
                    quoted = true;
                    item.Append(c);
                    continue;
                case '(':
                    comment++;
                    break;
                case ')' when comment > 0:
                    comment--;
                    break;
                case '<' when comment == 0:
                    inAngle = true;
                    break;
                case '>' when comment == 0:
                    inAngle = false;
                    break;
                case ':' when comment == 0 && !inAngle && !inGroup:
                    list.Add(new Entry(null, item.ToString().Trim().Trim('"'), false));
                    item.Clear();
                    inGroup = true;
                    continue;
                case ';' when comment == 0 && !inAngle && inGroup:
                    Flush();
                    list.Add(new Entry(null, null, true));
                    inGroup = false;
                    continue;
                case ',' when comment == 0 && !inAngle:
                    Flush();
                    continue;
            }

            item.Append(c);
        }

        Flush();
        if (inGroup)
        {
            list.Add(new Entry(null, null, true));
        }

        return list;
    }

    private static ImapResponseWriter Addresses(ImapResponseWriter w, List<Entry> list)
    {
        if (list.Count == 0)
        {
            return w.Raw("NIL");
        }

        w.Raw("(");
        foreach (var e in list)
        {
            if (e.GroupStart is { } g)
            {
                w.Raw("(NIL NIL ").Str(g).Raw(" NIL)"); // RFC 3501 7.4.2: group start: host NIL, mailbox = group name
                continue;
            }

            if (e.GroupEnd)
            {
                w.Raw("(NIL NIL NIL NIL)");
                continue;
            }

            var a = e.Mailbox!;
            var at = a.Address.LastIndexOf('@');
            var local = at < 0 ? a.Address : a.Address[..at];
            var host = at < 0 ? null : a.Address[(at + 1)..];
            w.Raw("(").Str(EncodeName(a.Name)).Raw(" ").Str(e.Route).Raw(" ").Str(local).Raw(" ").Str(host).Raw(")");
        }

        return w.Raw(")");
    }

    /// <summary>Display names go back to RFC 2047 encoded words when not ASCII (IMAP strings are 7-bit without UTF8=ACCEPT).</summary>
    private static string? EncodeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return Ascii.IsValid(name) ? name : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "?=";
    }

    // ---- BODY / BODYSTRUCTURE ----

    public void BodyStructure(ImapResponseWriter w, bool extensible) => Structure(w, _message.Root, extensible);

    private void Structure(ImapResponseWriter w, MimeEntity e, bool ext)
    {
        if (e.IsMultipart)
        {
            w.Raw("(");
            foreach (var c in e.Children)
            {
                Structure(w, c, ext);
            }

            w.Raw(" ").Str(Sub(e.MediaType).ToUpperInvariant());
            if (ext)
            {
                w.Raw(" ");
                Params(w, e.ContentType.Parameters);
                w.Raw(" ");
                Disposition(w, e);
                w.Raw(" NIL NIL");
            }

            w.Raw(")");
            return;
        }

        var (type, sub) = (MainType(e.MediaType), Sub(e.MediaType));
        w.Raw("(").Str(type.ToUpperInvariant()).Raw(" ").Str(sub.ToUpperInvariant()).Raw(" ");
        Params(w, e.ContentType.Parameters);
        w.Raw(" ").Str(e.Headers.Text("Content-ID")).Raw(" ").Str(e.Headers.Text("Content-Description")).Raw(" ")
            .Str((e.TransferEncoding ?? "7bit").ToUpperInvariant()).Raw(" ").Raw((End(e) - e.BodyOffset).ToString(CultureInfo.InvariantCulture));
        if (e.IsMessage)
        {
            var inner = e.Children[0];
            w.Raw(" ");
            Envelope(w, inner);
            w.Raw(" ");
            Structure(w, inner, ext);
            w.Raw(" ").Raw(Lines(e).ToString(CultureInfo.InvariantCulture));
        }
        else if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            w.Raw(" ").Raw(Lines(e).ToString(CultureInfo.InvariantCulture));
        }

        if (ext)
        {
            w.Raw(" ").Str(e.Headers.Text("Content-MD5")).Raw(" ");
            Disposition(w, e);
            w.Raw(" ").Str(e.Headers.Text("Content-Language")).Raw(" ").Str(e.Headers.Text("Content-Location"));
        }

        w.Raw(")");
    }

    private static void Params(ImapResponseWriter w, IEnumerable<KeyValuePair<string, string>> ps)
    {
        var list = ps.ToList();
        if (list.Count == 0)
        {
            w.Raw("NIL");
            return;
        }

        w.Raw("(");
        for (var i = 0; i < list.Count; i++)
        {
            w.Raw(i == 0 ? "" : " ").Str(list[i].Key.ToUpperInvariant()).Raw(" ").Str(EncodeName(list[i].Value) ?? "");
        }

        w.Raw(")");
    }

    private static void Disposition(ImapResponseWriter w, MimeEntity e)
    {
        if (e.ContentDisposition is not { } d)
        {
            w.Raw("NIL");
            return;
        }

        w.Raw("(").Str(d.Value.ToUpperInvariant()).Raw(" ");
        Params(w, d.Parameters);
        w.Raw(")");
    }

    private static string MainType(string mediaType) => mediaType.Split('/')[0];

    private static string Sub(string mediaType) => mediaType.Contains('/', StringComparison.Ordinal) ? mediaType[(mediaType.IndexOf('/', StringComparison.Ordinal) + 1)..] : "plain";

    private long Lines(MimeEntity e)
    {
        long n = 0;
        for (var i = e.BodyOffset; i < End(e) && i < raw.LongLength; i++)
        {
            if (raw[i] == '\n')
            {
                n++;
            }
        }

        return n;
    }

    // ---- BODY[section] ----

    /// <summary>
    /// The bytes of a section ("", "HEADER", "TEXT", "1.2", "1.2.MIME", "3.HEADER.FIELDS (FROM TO)") or null when the
    /// part does not exist (the response is then an empty string, RFC 3501 6.4.5).
    /// </summary>
    public byte[]? Section(string section)
    {
        var s = section.Trim();
        if (s.Length == 0)
        {
            return raw;
        }

        // Split "1.2.HEADER.FIELDS (A B)" into the part numbers and the text specifier.
        var entity = _message.Root;
        var isRoot = true;
        var rest = s;
        while (rest.Length > 0 && char.IsAsciiDigit(rest[0]))
        {
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            var numText = dot < 0 ? rest : rest[..dot];
            if (!int.TryParse(numText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return null;
            }

            var next = Part(entity, n, isRoot);
            if (next is null)
            {
                return null;
            }

            entity = next;
            isRoot = false;
            rest = dot < 0 ? "" : rest[(dot + 1)..];
        }

        if (rest.Length == 0)
        {
            return Slice(entity.BodyOffset, End(entity));
        }

        var upper = rest.ToUpperInvariant();
        if (upper == "MIME")
        {
            return isRoot ? null : Slice(entity.HeaderOffset, entity.BodyOffset);
        }

        // HEADER/TEXT of a part apply to an attached message (message/rfc822).
        var target = isRoot ? entity : entity.IsMessage ? entity.Children[0] : null;
        if (target is null)
        {
            return null;
        }

        if (upper == "HEADER")
        {
            return Slice(target.HeaderOffset, target.BodyOffset);
        }

        if (upper == "TEXT")
        {
            return Slice(target.BodyOffset, End(target));
        }

        if (upper.StartsWith("HEADER.FIELDS", StringComparison.Ordinal))
        {
            var not = upper.StartsWith("HEADER.FIELDS.NOT", StringComparison.Ordinal);
            var open = rest.IndexOf('(', StringComparison.Ordinal);
            var close = rest.LastIndexOf(')');
            if (open < 0 || close < open)
            {
                return null;
            }

            var names = rest[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim('"')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return FilterHeader(Slice(target.HeaderOffset, target.BodyOffset), names, not);
        }

        return null;
    }

    private static MimeEntity? Part(MimeEntity e, int n, bool isRoot)
    {
        if (e.IsMultipart)
        {
            return n <= e.Children.Count ? e.Children[n - 1] : null;
        }

        if (e.IsMessage && !isRoot)
        {
            return Part(e.Children[0], n, isRoot: true);
        }

        return n == 1 ? e : null;
    }

    private byte[] Slice(long from, long to)
    {
        from = Math.Clamp(from, 0, raw.LongLength);
        to = Math.Clamp(to, from, raw.LongLength);
        return raw.AsSpan((int)from, (int)(to - from)).ToArray();
    }

    /// <summary>Header lines kept (or dropped) by name, with their folded continuation lines, plus the closing blank line.</summary>
    private static byte[] FilterHeader(byte[] header, HashSet<string> names, bool not)
    {
        var text = Encoding.Latin1.GetString(header);
        var sb = new StringBuilder();
        var keep = false;
        foreach (var line in text.Split("\r\n"))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] is not (' ' or '\t'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var name = colon < 0 ? line : line[..colon].Trim();
                keep = names.Contains(name) != not;
            }

            if (keep)
            {
                sb.Append(line).Append("\r\n");
            }
        }

        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public long HeaderLength => _message.Root.BodyOffset - _message.Root.HeaderOffset;
}
