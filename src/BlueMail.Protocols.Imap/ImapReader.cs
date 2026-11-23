using System.Globalization;
using System.Text;

namespace BlueMail.Protocols.Imap;

/// <summary>
/// A logical IMAP line: text pieces (without CRLF) with the literal that follows each piece ending in <c>{n}</c>,
/// <c>{n+}</c> or <c>~{n}</c> (RFC 3501 4.3, RFC 7888 LITERAL+, RFC 3516 literal8). Literal i follows Text[i].
/// </summary>
public sealed class ImapLine : IDisposable
{
    public List<byte[]> Text { get; } = [];
    public List<ImapLiteralData> Literals { get; } = [];

    public void Dispose()
    {
        foreach (var l in Literals)
        {
            l.Dispose();
        }
    }

    /// <summary>For logs: the text with literals shown as their size.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Text.Count; i++)
        {
            sb.Append(Encoding.UTF8.GetString(Text[i]));
            if (i < Literals.Count)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<{Literals[i].Length} bytes>");
            }
        }

        return sb.ToString();
    }
}

public sealed record ImapReaderOptions
{
    /// <summary>Longest text part of one line (RFC 7162 3.2.1 recommends accepting at least 8192 octets).</summary>
    public int MaxLineLength { get; init; } = 64 * 1024;

    /// <summary>Literals larger than this go to a temporary file instead of memory.</summary>
    public long SpoolThreshold { get; init; } = 1024 * 1024;

    public long MaxLiteralLength { get; init; } = 200L * 1024 * 1024;
}

/// <summary>Reads logical lines from an IMAP connection; used by the client and (Phase 11) the server.</summary>
public sealed class ImapReader(Stream stream, ImapReaderOptions? options = null)
{
    private readonly ImapReaderOptions _options = options ?? new ImapReaderOptions();
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public Stream Stream { get; set; } = stream;

    /// <summary>True when bytes beyond the last line are buffered (checked before a TLS upgrade).</summary>
    public bool HasBufferedData => _start < _end;

    /// <summary>
    /// Reads one logical line. <paramref name="beforeLiteral"/> is called with the size and the non-synchronising flag
    /// before literal bytes are read (a server sends "+ Ready" there for synchronising literals).
    /// Returns null at end of stream.
    /// </summary>
    public async Task<ImapLine?> ReadLineAsync(Func<long, bool, ValueTask>? beforeLiteral, CancellationToken ct)
    {
        var line = new ImapLine();
        try
        {
            while (true)
            {
                var text = await ReadTextAsync(ct);
                if (text is null)
                {
                    line.Dispose();
                    return line.Text.Count == 0 ? null : throw new ImapProtocolException("Connection closed inside a command.");
                }

                line.Text.Add(text);
                if (!LiteralAtEnd(text, out var size, out var nonSync))
                {
                    return line;
                }

                if (size > _options.MaxLiteralLength)
                {
                    throw new ImapProtocolException("Literal too large.");
                }

                if (beforeLiteral is not null)
                {
                    await beforeLiteral(size, nonSync);
                }

                line.Literals.Add(await ReadLiteralAsync(size, ct));
            }
        }
        catch
        {
            line.Dispose();
            throw;
        }
    }

    /// <summary>True when the text ends in <c>{123}</c>, <c>{123+}</c> or <c>~{123}</c>.</summary>
    internal static bool LiteralAtEnd(ReadOnlySpan<byte> text, out long size, out bool nonSync)
    {
        size = 0;
        nonSync = false;
        if (text.Length < 3 || text[^1] != (byte)'}')
        {
            return false;
        }

        var open = text.LastIndexOf((byte)'{');
        if (open < 0)
        {
            return false;
        }

        var inner = text[(open + 1)..^1];
        if (inner.Length > 0 && inner[^1] == (byte)'+')
        {
            nonSync = true;
            inner = inner[..^1];
        }

        if (inner.Length is 0 or > 12)
        {
            return false;
        }

        foreach (var b in inner)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            size = size * 10 + (b - '0');
        }

        return true;
    }

    private async Task<byte[]?> ReadTextAsync(CancellationToken ct)
    {
        using var acc = new MemoryStream();
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                acc.Write(_buffer, _start, i - _start);
                _start = i + 1;
                var bytes = acc.ToArray();
                // Lines end in CRLF; a bare LF is accepted from lenient peers.
                return bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes;
            }

            acc.Write(_buffer, _start, _end - _start);
            _start = _end = 0;
            if (acc.Length > _options.MaxLineLength)
            {
                throw new ImapProtocolException("Line too long.");
            }

            var n = await Stream.ReadAsync(_buffer, ct);
            if (n == 0)
            {
                return acc.Length == 0 ? null : throw new ImapProtocolException("Connection closed inside a line.");
            }

            _end = n;
        }
    }

    private async Task<ImapLiteralData> ReadLiteralAsync(long size, CancellationToken ct)
    {
        if (size <= _options.SpoolThreshold)
        {
            var bytes = new byte[size];
            await FillAsync(bytes, ct);
            return new ImapLiteralData(bytes);
        }

        // Streamed to disk: a 50 MB message never sits in memory.
        var path = Path.Combine(Path.GetTempPath(), "bm-imap-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var left = size;
                while (left > 0)
                {
                    if (_start == _end)
                    {
                        var n = await Stream.ReadAsync(_buffer, ct);
                        if (n == 0)
                        {
                            throw new ImapProtocolException("Connection closed inside a literal.");
                        }

                        _start = 0;
                        _end = n;
                    }

                    var take = (int)Math.Min(left, _end - _start);
                    await file.WriteAsync(_buffer.AsMemory(_start, take), ct);
                    _start += take;
                    left -= take;
                }
            }

            return new ImapLiteralData(path, size);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private async Task FillAsync(byte[] target, CancellationToken ct)
    {
        var at = 0;
        while (at < target.Length)
        {
            if (_start == _end)
            {
                var n = await Stream.ReadAsync(_buffer, ct);
                if (n == 0)
                {
                    throw new ImapProtocolException("Connection closed inside a literal.");
                }

                _start = 0;
                _end = n;
            }

            var take = Math.Min(target.Length - at, _end - _start);
            Buffer.BlockCopy(_buffer, _start, target, at, take);
            _start += take;
            at += take;
        }
    }
}
