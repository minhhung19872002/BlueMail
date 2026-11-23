using System.Security.Cryptography;
using System.Text;

namespace BlueMail.Dkim;

public enum Canon { Simple, Relaxed }

/// <summary>A header field exactly as received: the full "Name: value" bytes including folding, without the final CRLF.</summary>
public sealed class RawHeader(byte[] raw)
{
    public byte[] Raw { get; } = raw;

    public string Name { get; } = Encoding.ASCII.GetString(raw, 0, Math.Max(0, Array.IndexOf(raw, (byte)':'))).Trim();

    /// <summary>Value after the colon, unfolded, as text (UTF-8 when valid, else Latin-1 byte-for-byte).</summary>
    public string Value
    {
        get
        {
            var colon = Array.IndexOf(Raw, (byte)':');
            var v = Raw.AsSpan(colon + 1);
            var s = System.Text.Unicode.Utf8.IsValid(v) ? Encoding.UTF8.GetString(v) : Encoding.Latin1.GetString(v);
            return s.Replace("\r\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();
        }
    }
}

/// <summary>Splits a message into raw header fields and the body offset, without interpreting anything.</summary>
public static class RawMessage
{
    public const int MaxHeaderBytes = 512 * 1024;

    /// <summary>Reads the header block from <paramref name="stream"/>; the stream is left at the first body byte.</summary>
    public static List<RawHeader> ReadHeaders(Stream stream)
    {
        var headers = new List<RawHeader>();
        var current = new List<byte>();
        var line = new List<byte>();
        var total = 0;
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            if (++total > MaxHeaderBytes)
            {
                throw new InvalidDataException("Header block too large.");
            }

            if (b != '\n')
            {
                line.Add((byte)b);
                continue;
            }

            if (line.Count > 0 && line[^1] == '\r')
            {
                line.RemoveAt(line.Count - 1);
            }

            if (line.Count == 0)
            {
                break; // end of headers
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                current.AddRange("\r\n"u8.ToArray());
                current.AddRange(line);
            }
            else
            {
                if (current.Count > 0)
                {
                    headers.Add(new RawHeader([.. current]));
                }

                current = [.. line];
            }

            line.Clear();
        }

        if (current.Count > 0)
        {
            headers.Add(new RawHeader([.. current]));
        }

        return headers;
    }
}

/// <summary>RFC 6376 3.4 canonicalization.</summary>
public static class Canonicalizer
{
    /// <summary>One header field canonicalised, followed by CRLF unless <paramref name="noCrlf"/>.</summary>
    public static byte[] Header(RawHeader h, Canon c, bool noCrlf = false) => Header(h.Raw, c, noCrlf);

    public static byte[] Header(ReadOnlySpan<byte> raw, Canon c, bool noCrlf = false)
    {
        if (c == Canon.Simple)
        {
            var r = new byte[raw.Length + (noCrlf ? 0 : 2)];
            raw.CopyTo(r);
            if (!noCrlf)
            {
                r[^2] = (byte)'\r';
                r[^1] = (byte)'\n';
            }

            return r;
        }

        // relaxed (3.4.2): lower-case name, unfold, WSP runs -> one SP, no WSP at end or around the colon
        var colon = raw.IndexOf((byte)':');
        var sb = new List<byte>(raw.Length);
        foreach (var ch in raw[..colon].TrimEnd(" \t"u8))
        {
            sb.Add(ch is >= (byte)'A' and <= (byte)'Z' ? (byte)(ch + 32) : ch);
        }

        sb.Add((byte)':');
        var value = new List<byte>(raw.Length - colon);
        var ws = false;
        foreach (var ch in raw[(colon + 1)..])
        {
            if (ch is (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            if (ch is (byte)' ' or (byte)'\t')
            {
                ws = true;
                continue;
            }

            if (ws && value.Count > 0)
            {
                value.Add((byte)' ');
            }

            ws = false;
            value.Add(ch);
        }

        sb.AddRange(value);
        if (!noCrlf)
        {
            sb.Add((byte)'\r');
            sb.Add((byte)'\n');
        }

        return [.. sb];
    }
}

/// <summary>
/// Streaming body canonicalisation + hash (RFC 6376 3.4.3/3.4.4, 3.7). Feed the body in any chunk sizes;
/// bare LF is treated as CRLF. Trailing empty lines are dropped. <see cref="Limit"/> caps the bytes hashed
/// (used only when signing with l=; verification ignores l=).
/// </summary>
public sealed class BodyHasher : IDisposable
{
    private readonly Canon _canon;
    private readonly IncrementalHash _hash;
    private readonly List<byte> _line = new(1024);
    private int _pendingEmptyLines;
    private long _hashed;
    private bool _anyContent;

    public BodyHasher(Canon canon, HashAlgorithmName algorithm)
    {
        _canon = canon;
        _hash = IncrementalHash.CreateHash(algorithm);
    }

    public long? Limit { get; init; }

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == '\n')
            {
                if (_line.Count > 0 && _line[^1] == '\r')
                {
                    _line.RemoveAt(_line.Count - 1);
                }

                EndLine();
                continue;
            }

            _line.Add(b);
        }
    }

    private void EndLine()
    {
        var content = _canon == Canon.Relaxed ? Relax(_line) : _line;
        if (content.Count == 0)
        {
            _pendingEmptyLines++;
        }
        else
        {
            for (var i = 0; i < _pendingEmptyLines; i++)
            {
                Write("\r\n"u8);
            }

            _pendingEmptyLines = 0;
            Write(content.ToArray());
            Write("\r\n"u8);
            _anyContent = true;
        }

        _line.Clear();
    }

    private static List<byte> Relax(List<byte> line)
    {
        var r = new List<byte>(line.Count);
        var ws = false;
        foreach (var b in line)
        {
            if (b is (byte)' ' or (byte)'\t')
            {
                ws = true;
                continue;
            }

            if (ws)
            {
                r.Add((byte)' ');
            }

            ws = false;
            r.Add(b);
        }

        return r; // trailing WSP dropped (ws pending at end is discarded)
    }

    private void Write(ReadOnlySpan<byte> s)
    {
        if (Limit is { } l)
        {
            var room = l - _hashed;
            if (room <= 0)
            {
                return;
            }

            s = s[..(int)Math.Min(s.Length, room)];
        }

        _hash.AppendData(s);
        _hashed += s.Length;
    }

    public long HashedLength => _hashed;

    public byte[] Finish()
    {
        if (_line.Count > 0)
        {
            EndLine(); // last line without CRLF gets one
        }

        if (!_anyContent && _canon == Canon.Simple)
        {
            Write("\r\n"u8); // simple: an empty body is one CRLF; relaxed: empty
        }

        return _hash.GetHashAndReset();
    }

    public void Dispose() => _hash.Dispose();

    public static byte[] Compute(Stream body, Canon canon, HashAlgorithmName alg, long? limit = null)
    {
        using var h = new BodyHasher(canon, alg) { Limit = limit };
        var buf = new byte[64 * 1024];
        int n;
        while ((n = body.Read(buf, 0, buf.Length)) > 0)
        {
            h.Append(buf.AsSpan(0, n));
        }

        return h.Finish();
    }
}
