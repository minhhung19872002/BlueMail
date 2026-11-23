using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace BlueMail.Protocols.Imap;

/// <summary>
/// sequence-set (RFC 3501 9): "1:4,7,9:*". '*' is the largest number in use; ranges may be written backwards ("5:2").
/// Also the RFC 5182 "$" (last search result) is not supported (SEARCHRES is not advertised).
/// </summary>
public sealed class ImapSequenceSet
{
    private readonly List<(long From, long To)> _ranges;

    private ImapSequenceSet(List<(long, long)> ranges) => _ranges = ranges;

    /// <summary>Ranges with '*' stored as long.MaxValue.</summary>
    public IReadOnlyList<(long From, long To)> Ranges => _ranges;

    public static bool TryParse(string text, out ImapSequenceSet set)
    {
        set = new ImapSequenceSet([]);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var part in text.Split(','))
        {
            var bits = part.Split(':');
            if (bits.Length is < 1 or > 2 || !Num(bits[0], out var a))
            {
                return false;
            }

            var b = a;
            if (bits.Length == 2 && !Num(bits[1], out b))
            {
                return false;
            }

            set._ranges.Add(a <= b ? (a, b) : (b, a));
        }

        return true;

        static bool Num(string s, out long v)
        {
            if (s == "*")
            {
                v = long.MaxValue;
                return true;
            }

            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v is > 0 and <= uint.MaxValue;
        }
    }

    /// <summary>Whether <paramref name="n"/> is in the set, '*' meaning <paramref name="max"/>.</summary>
    public bool Contains(long n, long max)
    {
        foreach (var (from, to) in _ranges)
        {
            var f = from == long.MaxValue ? max : from;
            var t = to == long.MaxValue ? max : to;
            if (f > t)
            {
                (f, t) = (t, f);
            }

            if (n >= f && n <= t)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Writes server responses: atoms, quoted strings or literals chosen by content, NIL, lists; COMPRESS=DEFLATE (RFC 4978)
/// by swapping the stream. Buffered: call <see cref="FlushAsync"/> after each response batch.
/// </summary>
public sealed class ImapResponseWriter(Stream stream) : IDisposable
{
    private readonly MemoryStream _buffer = new();
    private Stream _stream = stream;
    private DeflateStream? _deflate;

    public Stream BaseStream => _stream;

    public ImapResponseWriter Raw(string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        _buffer.Write(b);
        return this;
    }

    public ImapResponseWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public ImapResponseWriter Line(string text) => Raw(text).Raw("\r\n");

    /// <summary>A string: quoted when printable ASCII without quotes problems, otherwise a literal.</summary>
    public ImapResponseWriter Str(string? value)
    {
        if (value is null)
        {
            return Raw("NIL");
        }

        if (ImapArgs.TryQuote(value) is { } q && value.Length < 1024)
        {
            return Raw(q);
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Raw("{" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "}\r\n").Bytes(bytes);
    }

    public ImapResponseWriter Literal(ReadOnlySpan<byte> bytes) =>
        Raw("{" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "}\r\n").Bytes(bytes);

    public int Pending => (int)_buffer.Length;

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var target = (Stream?)_deflate ?? _stream;
        _buffer.Position = 0;
        await _buffer.CopyToAsync(target, ct);
        _buffer.SetLength(0);
        await target.FlushAsync(ct); // DeflateStream.Flush is a sync flush: the client can decode everything sent
    }

    /// <summary>After "OK DEFLATE active": everything written from now on is compressed (RFC 4978).</summary>
    public void StartDeflate() => _deflate = new DeflateStream(_stream, CompressionLevel.Fastest, leaveOpen: true);

    public bool Compressed => _deflate is not null;

    public void ReplaceStream(Stream s) => _stream = s;

    public void Dispose()
    {
        _deflate?.Dispose();
        _buffer.Dispose();
    }
}
