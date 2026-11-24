using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// One node of the MIME tree. Bodies are not held in memory: the entity records byte offsets into the
/// original message (headers start, body start, body end), so a single part can be read later from a
/// file or from S3 with a range request.
/// </summary>
public sealed class MimeEntity
{
    public HeaderList Headers { get; } = [];
    public HeaderValueWithParameters ContentType { get; internal set; } = HeaderValueWithParameters.Parse("text/plain; charset=us-ascii");
    public HeaderValueWithParameters? ContentDisposition { get; internal set; }
    public string? TransferEncoding { get; internal set; }

    /// <summary>IMAP-style part path: "" for the message, "1", "1.2"... (RFC 3501 6.4.5).</summary>
    public string PartPath { get; internal set; } = "";

    public int Depth { get; internal set; }
    public long HeaderOffset { get; internal set; }
    public long BodyOffset { get; internal set; }
    public long BodyEndOffset { get; internal set; }
    public List<MimeEntity> Children { get; } = [];

    /// <summary>The charset used for raw 8-bit header bytes of this entity (its own declared charset).</summary>
    internal string? HeaderFallbackCharset { get; set; }

    public string MediaType => ContentType.Value;
    public bool IsMultipart => MediaType.StartsWith("multipart/", StringComparison.Ordinal) && Children.Count > 0;
    public bool IsMessage => MediaType is "message/rfc822" or "message/global" && Children.Count == 1;
    public bool IsLeaf => Children.Count == 0;
    public string? Charset => ContentType["charset"];
    public long BodyLength => BodyEndOffset - BodyOffset;

    /// <summary>File name: Content-Disposition filename (RFC 2183/2231), else Content-Type name.</summary>
    public string? FileName
    {
        get
        {
            var n = ContentDisposition?["filename"] ?? ContentType["name"];
            return string.IsNullOrWhiteSpace(n) ? null : MimeText.SanitizeFileName(n);
        }
    }

    public string? ContentId => MessageIds.ParseSingle(Headers.Text("Content-ID")) ?? Headers.Text("Content-ID")?.Trim('<', '>', ' ');

    public bool IsAttachment =>
        IsLeaf && (ContentDisposition?.Value == "attachment" || (FileName is not null && ContentDisposition?.Value != "inline" && !MediaType.StartsWith("text/", StringComparison.Ordinal)));

    public bool IsInline => ContentDisposition?.Value == "inline";

    /// <summary>A whole message carried as a file (forward as attachment, .eml): message/rfc822 not marked inline.</summary>
    public bool IsAttachedMessage => IsMessage && ContentDisposition?.Value != "inline";

    /// <summary>Like <see cref="Walk"/> but an attached message is one entity: its own body and files are not this message's.</summary>
    public IEnumerable<MimeEntity> WalkOwn() => WalkOwn(true);

    private IEnumerable<MimeEntity> WalkOwn(bool top)
    {
        yield return this;
        if (IsAttachedMessage && !top)
        {
            yield break;
        }

        foreach (var c in Children)
        {
            foreach (var d in c.WalkOwn(false))
            {
                yield return d;
            }
        }
    }

    public IEnumerable<MimeEntity> Walk()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.Walk())
            {
                yield return d;
            }
        }
    }

    /// <summary>The raw (still transfer-encoded) body bytes from a seekable source.</summary>
    public Stream OpenRawBody(Stream source) => new SliceStream(source, BodyOffset, BodyLength);

    /// <summary>The transfer-decoded body.</summary>
    public Stream OpenDecodedBody(Stream source) => TransferDecoding.Decode(OpenRawBody(source), TransferEncoding);

    public byte[] ReadDecodedBody(Stream source)
    {
        using var s = OpenDecodedBody(source);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Text of a text/* part: transfer-decoded, charset-decoded with controlled guessing, format=flowed
    /// (RFC 3676) reflowed, line endings normalised to LF.
    /// </summary>
    public string ReadText(Stream source)
    {
        var text = Charsets.Decode(ReadDecodedBody(source), Charset);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.Equals(ContentType["format"], "flowed", StringComparison.OrdinalIgnoreCase))
        {
            text = FormatFlowed.Decode(text, string.Equals(ContentType["delsp"], "yes", StringComparison.OrdinalIgnoreCase));
        }

        return text;
    }
}

/// <summary>A parsed message: the root entity plus convenience accessors and the defects found.</summary>
public sealed class MimeMessage
{
    internal MimeMessage(MimeEntity root, List<MimeDefect> defects, long length)
    {
        Root = root;
        Defects = defects;
        Length = length;
    }

    public MimeEntity Root { get; }
    public IReadOnlyList<MimeDefect> Defects { get; }
    public long Length { get; }
    public HeaderList Headers => Root.Headers;

    private string? Fallback => Root.HeaderFallbackCharset;

    public string? Subject => Headers.Text("Subject", Fallback);
    public IReadOnlyList<MailboxAddress> From => AddressParser.Parse(Headers["From"]?.Value(Fallback));
    public IReadOnlyList<MailboxAddress> Sender => AddressParser.Parse(Headers["Sender"]?.Value(Fallback));
    public IReadOnlyList<MailboxAddress> ReplyTo => AddressParser.Parse(Headers["Reply-To"]?.Value(Fallback));
    public IReadOnlyList<MailboxAddress> To => Addresses("To");
    public IReadOnlyList<MailboxAddress> Cc => Addresses("Cc");
    public IReadOnlyList<MailboxAddress> Bcc => Addresses("Bcc");
    public DateTimeOffset? Date => DateParser.Parse(Headers["Date"]?.Value(Fallback));
    public string? MessageId => MessageIds.ParseSingle(Headers["Message-ID"]?.Value(Fallback));
    public string? InReplyTo => MessageIds.ParseList(Headers["In-Reply-To"]?.Value(Fallback)).LastOrDefault();
    public IReadOnlyList<string> References => MessageIds.ParseList(Headers["References"]?.Value(Fallback));

    /// <summary>Every To (or Cc...) field of the message, in order (duplicated fields happen).</summary>
    private IReadOnlyList<MailboxAddress> Addresses(string name) =>
        Headers.All(name).SelectMany(h => AddressParser.Parse(h.Value(Fallback))).ToList();

    public static MimeMessage Parse(Stream stream, MimeLimits? limits = null) => new MimeParser(stream, limits ?? MimeLimits.Default).Parse();

    public static MimeMessage Parse(byte[] data, MimeLimits? limits = null) => Parse(new MemoryStream(data, writable: false), limits);

    /// <summary>Best body text: text/plain if present, else text/html converted to text.</summary>
    public string BodyText(Stream source)
    {
        var plain = Root.Walk().FirstOrDefault(e => e.IsLeaf && e.MediaType == "text/plain" && !e.IsAttachment);
        if (plain is not null)
        {
            return plain.ReadText(source);
        }

        var html = Root.Walk().FirstOrDefault(e => e.IsLeaf && e.MediaType == "text/html" && !e.IsAttachment);
        return html is null ? "" : HtmlToText.Convert(html.ReadText(source));
    }
}

internal static class MimeText
{
    /// <summary>Removes path parts and control characters from attachment names (never trust a filename).</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString().Trim();
        var slash = s.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < s.Length - 1 ? s[(slash + 1)..] : s;
    }
}

/// <summary>Read-only window [offset, offset+length) of a seekable stream.</summary>
internal sealed class SliceStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _pos;

    public SliceStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = Math.Max(0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _pos;
        if (remaining <= 0)
        {
            return 0;
        }

        _inner.Position = _start + _pos;
        var n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        _pos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _length); }
    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => _length + offset,
    };

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The slice does not own the source stream.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
