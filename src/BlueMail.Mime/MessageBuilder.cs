using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlueMail.Mime;

public sealed class BuilderAttachment
{
    public required string FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Content source; opened once when the message is written (streamed, not buffered).</summary>
    public required Func<Stream> Open { get; init; }

    /// <summary>Set for images shown in the body (multipart/related, referenced as cid:ContentId).</summary>
    public string? ContentId { get; init; }
}

/// <summary>
/// Builds standards-conforming messages (PROMPT 2.6.1, 6.4): multipart/alternative (text + HTML),
/// multipart/related for inline images, multipart/mixed for attachments; CRLF line endings; no line over
/// 998 bytes; RFC 2047 for non-ASCII header text, RFC 2231 for non-ASCII file names; raw UTF-8 addresses only
/// for internationalised addresses (RFC 6532, requires SMTPUTF8); random boundaries.
/// </summary>
public sealed class MessageBuilder
{
    public required MailboxAddress From { get; init; }
    public MailboxAddress? Sender { get; init; }
    public IReadOnlyList<MailboxAddress> To { get; init; } = [];
    public IReadOnlyList<MailboxAddress> Cc { get; init; } = [];

    /// <summary>Written only when <see cref="IncludeBccHeader"/> (the sender's own copy); never sent to recipients.</summary>
    public IReadOnlyList<MailboxAddress> Bcc { get; init; } = [];

    public bool IncludeBccHeader { get; init; }
    public IReadOnlyList<MailboxAddress> ReplyTo { get; init; } = [];
    public string Subject { get; init; } = "";
    public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
    public required string MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<BuilderAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<(string Name, string Value)> ExtraHeaders { get; init; } = [];

    /// <summary>New globally unique Message-ID (RFC 5322 3.6.4) on the configured host name.</summary>
    public static string NewMessageId(string hostname) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "@" + hostname;

    /// <summary>True when any address needs SMTPUTF8 (non-ASCII local part or domain).</summary>
    public bool RequiresSmtpUtf8 =>
        new[] { From }.Concat(To).Concat(Cc).Concat(Bcc).Concat(ReplyTo).Any(a => !Ascii.IsValid(a.Address));

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        WriteTo(ms);
        return ms.ToArray();
    }

    public void WriteTo(Stream output)
    {
        using var w = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        WriteHeader(w, "Date", DateParser.Format(Date));
        WriteHeader(w, "From", Addresses([From]));
        if (Sender is not null)
        {
            WriteHeader(w, "Sender", Addresses([Sender]));
        }

        if (To.Count > 0)
        {
            WriteHeader(w, "To", Addresses(To));
        }

        if (Cc.Count > 0)
        {
            WriteHeader(w, "Cc", Addresses(Cc));
        }

        if (IncludeBccHeader && Bcc.Count > 0)
        {
            WriteHeader(w, "Bcc", Addresses(Bcc));
        }

        if (ReplyTo.Count > 0)
        {
            WriteHeader(w, "Reply-To", Addresses(ReplyTo));
        }

        WriteHeader(w, "Message-ID", "<" + MessageId + ">");
        if (InReplyTo is not null)
        {
            WriteHeader(w, "In-Reply-To", "<" + InReplyTo + ">");
        }

        if (References.Count > 0)
        {
            WriteHeader(w, "References", string.Join(' ', References.Select(r => "<" + r + ">")));
        }

        WriteHeader(w, "Subject", EncodedWords.Encode(Subject));
        foreach (var (name, value) in ExtraHeaders)
        {
            WriteHeader(w, name, Ascii.IsValid(value) ? value : EncodedWords.Encode(value));
        }

        WriteHeader(w, "MIME-Version", "1.0");

        var inline = Attachments.Where(a => a.ContentId is not null).ToList();
        var files = Attachments.Where(a => a.ContentId is null).ToList();
        var body = new Node(BodyNode(inline));
        WriteEntity(w, output, files.Count > 0 ? Mixed(body, files) : body);
        w.Flush();
    }

    // ---------------- structure ----------------

    private sealed class Node(object content)
    {
        public object Content { get; } = content; // Leaf, Multipart
    }

    private sealed record Leaf(string ContentType, Func<Stream> Open, bool IsText, string? FileName, string? ContentId, bool Inline);

    private sealed record Multipart(string Subtype, IReadOnlyList<Node> Parts);

    private object BodyNode(List<BuilderAttachment> inline)
    {
        Leaf? text = TextBody is null ? null : TextLeaf("text/plain", TextBody);
        Leaf? html = HtmlBody is null ? null : TextLeaf("text/html", HtmlBody);
        object alt = text is not null && html is not null
            ? new Multipart("alternative", [new Node(text), new Node(html)])
            : (object?)html ?? text ?? TextLeaf("text/plain", "");
        if (inline.Count == 0)
        {
            return alt;
        }

        var related = new List<Node> { new(alt) };
        related.AddRange(inline.Select(a => new Node(new Leaf(a.ContentType, a.Open, false, a.FileName, a.ContentId, true))));
        return new Multipart("related", related);
    }

    private static Node Mixed(Node body, List<BuilderAttachment> files)
    {
        var parts = new List<Node> { body };
        parts.AddRange(files.Select(a => new Node(new Leaf(a.ContentType, a.Open, false, a.FileName, null, false))));
        return new Node(new Multipart("mixed", parts));
    }

    private static Leaf TextLeaf(string type, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return new Leaf(type + "; charset=utf-8", () => new MemoryStream(bytes, writable: false), true, null, null, false);
    }

    private static void WriteEntity(StreamWriter w, Stream output, Node node)
    {
        switch (node.Content)
        {
            case Multipart m:
                var boundary = "=_bm_" + Totp32(RandomNumberGenerator.GetBytes(18));
                WriteHeader(w, "Content-Type", $"multipart/{m.Subtype}; boundary=\"{boundary}\"" + (m.Subtype == "related" ? "; type=\"multipart/alternative\"" : ""));
                w.WriteLine();
                foreach (var part in m.Parts)
                {
                    w.WriteLine("--" + boundary);
                    WriteEntity(w, output, part);
                    w.WriteLine();
                }

                w.WriteLine("--" + boundary + "--");
                break;
            case Leaf leaf:
                WriteLeaf(w, output, leaf);
                break;
        }
    }

    private static void WriteLeaf(StreamWriter w, Stream output, Leaf leaf)
    {
        var contentType = leaf.ContentType;
        if (leaf.FileName is not null)
        {
            contentType += "; name=\"" + QuoteParam(EncodedWords.Encode(leaf.FileName)) + "\"";
        }

        WriteHeader(w, "Content-Type", contentType);
        if (leaf.FileName is not null || leaf.Inline)
        {
            var disposition = leaf.Inline ? "inline" : "attachment";
            WriteHeader(w, "Content-Disposition", disposition + (leaf.FileName is null ? "" : "; " + Rfc2231FileName(leaf.FileName)));
        }

        if (leaf.ContentId is not null)
        {
            WriteHeader(w, "Content-ID", "<" + leaf.ContentId + ">");
        }

        using var source = leaf.Open();
        if (leaf.IsText)
        {
            using var ms = new MemoryStream();
            source.CopyTo(ms);
            var bytes = ms.ToArray();
            var cte = ChooseTextEncoding(bytes);
            WriteHeader(w, "Content-Transfer-Encoding", cte);
            w.WriteLine();
            w.Write(cte switch
            {
                "7bit" => Encoding.ASCII.GetString(bytes),
                "quoted-printable" => TransferDecoding.EncodeQuotedPrintable(bytes),
                _ => TransferDecoding.EncodeBase64Lines(bytes),
            });
            if (cte == "7bit" && bytes.Length > 0 && !bytes.AsSpan().EndsWith("\r\n"u8))
            {
                w.WriteLine();
            }

            return;
        }

        WriteHeader(w, "Content-Transfer-Encoding", "base64");
        w.WriteLine();
        w.Flush();
        var buffer = new byte[57 * 256]; // multiple of 57 bytes = whole 76-char base64 lines
        int n;
        while ((n = ReadFull(source, buffer)) > 0)
        {
            w.Write(TransferDecoding.EncodeBase64Lines(buffer.AsSpan(0, n)));
        }
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>7bit for short-lined ASCII, quoted-printable for mostly-ASCII text, base64 otherwise.</summary>
    internal static string ChooseTextEncoding(ReadOnlySpan<byte> bytes)
    {
        var maxLine = 0;
        var current = 0;
        var nonAscii = 0;
        foreach (var b in bytes)
        {
            if (b == '\n')
            {
                maxLine = Math.Max(maxLine, current);
                current = 0;
                continue;
            }

            current++;
            if (b >= 128 || b == 0)
            {
                nonAscii++;
            }
        }

        maxLine = Math.Max(maxLine, current);
        if (nonAscii == 0 && maxLine <= 998)
        {
            return "7bit";
        }

        return nonAscii * 5 < bytes.Length ? "quoted-printable" : "base64";
    }

    // ---------------- headers ----------------

    private static string Addresses(IEnumerable<MailboxAddress> list) => string.Join(", ", list.Select(FormatAddress));

    internal static string FormatAddress(MailboxAddress a)
    {
        if (string.IsNullOrEmpty(a.Name))
        {
            return a.Address;
        }

        var name = Ascii.IsValid(a.Name) ? QuoteIfNeeded(a.Name) : EncodedWords.Encode(a.Name);
        return name + " <" + a.Address + ">";
    }

    private static string QuoteIfNeeded(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ' || "!#$%&'*+-/=?^_`{|}~".Contains(c)) && !name.Contains("=?", StringComparison.Ordinal)
            ? name
            : "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string QuoteParam(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>filename="ascii" or filename*0*=utf-8''... continuations of at most 60 encoded characters.</summary>
    internal static string Rfc2231FileName(string fileName)
    {
        if (Ascii.IsValid(fileName) && fileName.Length <= 60 && !fileName.Contains('"', StringComparison.Ordinal))
        {
            return "filename=\"" + fileName + "\"";
        }

        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(fileName.Normalize(NormalizationForm.FormC)))
        {
            var c = (char)b;
            encoded.Append(b < 128 && (char.IsAsciiLetterOrDigit(c) || "!#$&+-.^_`|~".Contains(c)) ? c.ToString() : "%" + b.ToString("X2", CultureInfo.InvariantCulture));
        }

        var s = encoded.ToString();
        var segments = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var len = Math.Min(60, s.Length - i);
            // never split a %XX escape
            if (i + len < s.Length)
            {
                var pct = s.LastIndexOf('%', i + len - 1, Math.Min(2, len));
                if (pct >= 0 && pct > i + len - 3)
                {
                    len = pct - i;
                }
            }

            segments.Add(s.Substring(i, len));
            i += len;
        }

        if (segments.Count == 1)
        {
            return "filename*=utf-8''" + segments[0];
        }

        return string.Join("; ", segments.Select((seg, idx) => $"filename*{idx}*=" + (idx == 0 ? "utf-8''" : "") + seg));
    }

    /// <summary>Writes "Name: value" folded at whitespace to keep lines short (RFC 5322 2.2.3).</summary>
    private static void WriteHeader(StreamWriter w, string name, string value)
    {
        var line = new StringBuilder(name).Append(": ");
        var lineLength = line.Length;
        var first = true;
        foreach (var word in value.Split(' '))
        {
            var bytes = Encoding.UTF8.GetByteCount(word);
            if (!first && lineLength + 1 + bytes > 78)
            {
                line.Append("\r\n ");
                lineLength = 1;
            }
            else if (!first)
            {
                line.Append(' ');
                lineLength++;
            }

            line.Append(word);
            lineLength += bytes;
            first = false;
        }

        w.Write(line);
        w.Write("\r\n");
    }

    private static string Totp32(byte[] data)
    {
        // base32 alphabet without padding; '=' prefix already makes the boundary impossible in base64/QP output
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return sb.ToString();
    }
}
