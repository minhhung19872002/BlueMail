using System.Buffers;
using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// Streaming MIME parser (RFC 5322, RFC 2045/2046). Reads the source once, line by line, keeping only the
/// current line in memory; records offsets instead of bodies. Tolerates bare LF, missing or mismatched
/// boundaries, header lines without a colon (they start the body, as in other mail software), and
/// enforces <see cref="MimeLimits"/>.
/// </summary>
internal sealed class MimeParser
{
    private readonly LineReader _reader;
    private readonly MimeLimits _limits;
    private readonly List<MimeDefect> _defects = [];
    private int _parts;
    private bool _bareLfReported;

    public MimeParser(Stream stream, MimeLimits limits)
    {
        _reader = new LineReader(stream, limits.MaxLineBytes);
        _limits = limits;
    }

    private enum End { Eof, Delimiter, Close }

    private readonly record struct Ending(End Kind, int BoundaryIndex);

    public MimeMessage Parse()
    {
        var root = new MimeEntity();
        ParseEntity(root, 0, [], "", null);
        return new MimeMessage(root, _defects, _reader.Position);
    }

    /// <summary>Parses one entity. Returns how it ended: EOF, or a boundary line of one of <paramref name="boundaries"/>.</summary>
    private Ending ParseEntity(MimeEntity e, int depth, List<string> boundaries, string path, string? parentMediaType)
    {
        _parts++;
        e.Depth = depth;
        e.PartPath = path;
        e.HeaderOffset = _reader.Position;

        var headerEnding = ReadHeaders(e, boundaries, out var bodyAlreadyStarted);
        ApplyHeaders(e, parentMediaType);
        if (headerEnding is { } he)
        {
            // A boundary line came before the blank line: the entity has headers only.
            e.BodyOffset = e.BodyEndOffset = _reader.LastLineStart;
            return he;
        }

        e.BodyOffset = bodyAlreadyStarted ? _reader.LastLineStart : _reader.Position;

        if (e.MediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = e.ContentType["boundary"];
            if (string.IsNullOrEmpty(boundary))
            {
                _defects.Add(new MimeDefect(MimeDefectKind.MissingBoundaryParameter, e.HeaderOffset));
            }
            else if (depth >= _limits.MaxDepth)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.DepthLimitReached, e.HeaderOffset));
            }
            else if (_parts >= _limits.MaxParts)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.PartLimitReached, e.HeaderOffset));
            }
            else
            {
                return ParseMultipart(e, depth, boundaries, boundary, path, bodyAlreadyStarted);
            }
        }
        else if (e.MediaType is "message/rfc822" or "message/global" && depth < _limits.MaxDepth && _parts < _limits.MaxParts
                 && !IsEncoded(e.TransferEncoding))
        {
            var inner = new MimeEntity();
            e.Children.Add(inner);
            var ending = ParseEntity(inner, depth + 1, boundaries, path.Length == 0 ? "1" : path + ".1", null);
            e.BodyEndOffset = inner.BodyEndOffset;
            return ending;
        }

        return ReadLeafBody(e, boundaries, bodyAlreadyStarted);
    }

    private static bool IsEncoded(string? cte) => cte is "base64" or "quoted-printable";

    private Ending ParseMultipart(MimeEntity e, int depth, List<string> boundaries, string boundary, string path, bool bodyAlreadyStarted)
    {
        var stack = new List<string>(boundaries) { boundary };
        var own = stack.Count - 1;

        // Preamble: everything up to the first delimiter is ignored.
        var first = SkipUntilBoundary(stack, bodyAlreadyStarted);
        if (first.Kind == End.Eof || first.BoundaryIndex != own)
        {
            if (first.Kind == End.Eof || first.BoundaryIndex < own)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.NoPartsFound, e.HeaderOffset));
            }

            e.BodyEndOffset = first.Kind == End.Eof ? _reader.Position : _reader.LastLineStart;
            return first;
        }

        if (first.Kind == End.Close)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.NoPartsFound, e.HeaderOffset));
            return FinishMultipart(e, stack, own);
        }

        var n = 0;
        while (true)
        {
            n++;
            var child = new MimeEntity();
            e.Children.Add(child);
            var childPath = path.Length == 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ending = ParseEntity(child, depth + 1, stack, childPath, e.MediaType);

            if (ending.Kind == End.Eof)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.MissingClosingBoundary, _reader.Position, boundary));
                e.BodyEndOffset = _reader.Position;
                return ending;
            }

            if (ending.BoundaryIndex < own)
            {
                // An ancestor's boundary closed us without our own close-delimiter.
                _defects.Add(new MimeDefect(MimeDefectKind.MissingClosingBoundary, _reader.LastLineStart, boundary));
                e.BodyEndOffset = _reader.LastLineStart;
                return ending;
            }

            if (ending.Kind == End.Close)
            {
                return FinishMultipart(e, stack, own);
            }

            if (_parts >= _limits.MaxParts)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.PartLimitReached, _reader.Position));
                var rest = SkipUntilBoundary(stack.Take(own).ToList(), false);
                e.BodyEndOffset = rest.Kind == End.Eof ? _reader.Position : _reader.LastLineStart;
                return rest;
            }
        }
    }

    /// <summary>After our close-delimiter: the epilogue runs until an ancestor boundary or EOF.</summary>
    private Ending FinishMultipart(MimeEntity e, List<string> stack, int own)
    {
        e.BodyEndOffset = _reader.Position;
        var ancestors = stack.Take(own).ToList();
        var epilogue = SkipUntilBoundary(ancestors, false);
        return epilogue;
    }

    private Ending SkipUntilBoundary(List<string> stack, bool currentLineIsFirst)
    {
        if (currentLineIsFirst && MatchBoundary(_reader.LastLine, stack) is { } m0)
        {
            return m0;
        }

        while (_reader.ReadLine())
        {
            NoteLineEnding();
            if (MatchBoundary(_reader.LastLine, stack) is { } m)
            {
                return m;
            }
        }

        return new Ending(End.Eof, -1);
    }

    private Ending ReadLeafBody(MimeEntity e, List<string> boundaries, bool bodyAlreadyStarted)
    {
        var lastContentEnd = e.BodyOffset;
        if (bodyAlreadyStarted)
        {
            if (MatchBoundary(_reader.LastLine, boundaries) is { } m0)
            {
                e.BodyEndOffset = e.BodyOffset;
                return m0;
            }

            lastContentEnd = _reader.LastContentEnd;
        }

        var any = bodyAlreadyStarted;
        while (_reader.ReadLine())
        {
            NoteLineEnding();
            if (boundaries.Count > 0 && MatchBoundary(_reader.LastLine, boundaries) is { } m)
            {
                // The line break before a boundary belongs to the boundary (RFC 2046 5.1.1).
                e.BodyEndOffset = any ? lastContentEnd : e.BodyOffset;
                return m;
            }

            any = true;
            lastContentEnd = _reader.LastContentEnd;
        }

        e.BodyEndOffset = _reader.Position;
        return new Ending(End.Eof, -1);
    }

    private static Ending? MatchBoundary(ReadOnlySpan<byte> line, List<string> boundaries)
    {
        if (line.Length < 3 || line[0] != '-' || line[1] != '-')
        {
            return null;
        }

        // Innermost first: a nested boundary that is a prefix of an outer one must still match the inner part.
        for (var i = boundaries.Count - 1; i >= 0; i--)
        {
            var b = boundaries[i];
            var rest = line[2..];
            if (rest.Length < b.Length || !Ascii.Equals(rest[..b.Length], b))
            {
                continue;
            }

            rest = rest[b.Length..];
            if (rest.Length >= 2 && rest[0] == '-' && rest[1] == '-')
            {
                return new Ending(End.Close, i);
            }

            if (rest.TrimEnd(" \t"u8).Length == 0)
            {
                return new Ending(End.Delimiter, i);
            }
        }

        return null;
    }

    /// <summary>Reads the header block. Returns an Ending if a boundary line interrupted it.</summary>
    private Ending? ReadHeaders(MimeEntity e, List<string> boundaries, out bool bodyAlreadyStarted)
    {
        bodyAlreadyStarted = false;
        var blockBytes = 0;
        string? name = null;
        List<byte>? value = null;

        void Commit()
        {
            if (name is null)
            {
                return;
            }

            if (e.Headers.Count < _limits.MaxHeaderCount)
            {
                e.Headers.Add(new HeaderField(name, [.. value!]));
            }
            else if (e.Headers.Count == _limits.MaxHeaderCount)
            {
                _defects.Add(new MimeDefect(MimeDefectKind.TooManyHeaders, _reader.Position));
            }

            name = null;
        }

        while (_reader.ReadLine())
        {
            NoteLineEnding();
            var line = _reader.LastLine;
            if (line.Length == 0)
            {
                Commit();
                return null; // blank line: body follows
            }

            if (boundaries.Count > 0 && MatchBoundary(line, boundaries) is { } m)
            {
                Commit();
                return m;
            }

            blockBytes += line.Length + 2;
            if (blockBytes > _limits.MaxHeaderBlockBytes)
            {
                Commit();
                _defects.Add(new MimeDefect(MimeDefectKind.HeaderBlockTooLarge, _reader.LastLineStart));
                bodyAlreadyStarted = true;
                return null;
            }

            if (line.Contains((byte)0))
            {
                _defects.Add(new MimeDefect(MimeDefectKind.NulCharacter, _reader.LastLineStart));
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                if (name is not null)
                {
                    value!.AddRange("\r\n"u8.ToArray());
                    value.AddRange(line.ToArray());
                }

                continue;
            }

            var colon = line.IndexOf((byte)':');
            var fieldName = colon > 0 ? line[..colon].TrimEnd(" \t"u8) : default;
            if (colon <= 0 || !IsFieldName(fieldName))
            {
                // Not a header field: the header section is over and this line is the first body line.
                Commit();
                _defects.Add(new MimeDefect(MimeDefectKind.HeaderWithoutColon, _reader.LastLineStart));
                bodyAlreadyStarted = true;
                return null;
            }

            Commit();
            name = Encoding.ASCII.GetString(fieldName);
            value = [.. line[(colon + 1)..]];
            if (!Charsets.IsAscii(line) && !Charsets.IsValidUtf8(line))
            {
                _defects.Add(new MimeDefect(MimeDefectKind.UndeclaredEightBitHeader, _reader.LastLineStart, name));
            }
        }

        Commit();
        return null; // end of input inside the headers: an empty body follows
    }

    private static bool IsFieldName(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var b in s)
        {
            if (b is < 33 or > 126)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyHeaders(MimeEntity e, string? parentMediaType)
    {
        var ct = e.Headers["Content-Type"];
        var defaultType = parentMediaType == "multipart/digest" ? "message/rfc822" : "text/plain; charset=us-ascii";
        var parsed = ct is null ? HeaderValueWithParameters.Parse(defaultType) : HeaderValueWithParameters.Parse(ct.Value(), null);
        if (!parsed.Value.Contains('/', StringComparison.Ordinal))
        {
            parsed = HeaderValueWithParameters.Parse(defaultType); // RFC 2045 5.2: invalid type means the default
        }

        e.ContentType = parsed;
        e.HeaderFallbackCharset = parsed["charset"];
        var cd = e.Headers["Content-Disposition"];
        e.ContentDisposition = cd is null ? null : HeaderValueWithParameters.Parse(cd.Value(e.HeaderFallbackCharset), e.HeaderFallbackCharset);
        e.TransferEncoding = e.Headers["Content-Transfer-Encoding"]?.Value().Trim().ToLowerInvariant();
    }

    private void NoteLineEnding()
    {
        if (_reader.LastLineHadBareLf && !_bareLfReported)
        {
            _bareLfReported = true;
            _defects.Add(new MimeDefect(MimeDefectKind.BareLineFeed, _reader.LastLineStart));
        }

        if (_reader.LastLineWasSplit)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.LineTooLong, _reader.LastLineStart));
        }
    }
}

/// <summary>
/// Reads lines terminated by CRLF or bare LF, tracking byte offsets. A line longer than the limit is
/// returned in pieces (no unbounded buffering).
/// </summary>
internal sealed class LineReader(Stream stream, int maxLine)
{
    private readonly byte[] _buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _bufStart;
    private int _bufEnd;
    private byte[] _line = new byte[1024];
    private int _lineLen;
    private bool _eof;

    /// <summary>Offset just after the last line read (including its line break).</summary>
    public long Position { get; private set; }
    public long LastLineStart { get; private set; }

    /// <summary>Offset of the end of the last line's content (before CR/LF).</summary>
    public long LastContentEnd { get; private set; }

    public bool LastLineHadBareLf { get; private set; }
    public bool LastLineWasSplit { get; private set; }
    public ReadOnlySpan<byte> LastLine => _line.AsSpan(0, _lineLen);

    public bool ReadLine()
    {
        _lineLen = 0;
        LastLineStart = Position;
        LastLineHadBareLf = false;
        LastLineWasSplit = false;
        while (true)
        {
            if (_bufStart == _bufEnd)
            {
                if (_eof || !Fill())
                {
                    if (_lineLen == 0)
                    {
                        LastContentEnd = Position;
                        return false;
                    }

                    LastContentEnd = Position;
                    return true; // last line without a line break
                }
            }

            var span = _buf.AsSpan(_bufStart, _bufEnd - _bufStart);
            var lf = span.IndexOf((byte)'\n');
            var take = lf >= 0 ? lf : span.Length;
            if (_lineLen + take > maxLine)
            {
                take = Math.Max(0, maxLine - _lineLen);
                Append(span[..take]);
                _bufStart += take;
                Position += take;
                LastContentEnd = Position;
                LastLineWasSplit = true;
                return true;
            }

            Append(span[..take]);
            _bufStart += take;
            Position += take;
            if (lf >= 0)
            {
                _bufStart++; // consume LF
                Position++;
                if (_lineLen > 0 && _line[_lineLen - 1] == '\r')
                {
                    _lineLen--;
                    LastContentEnd = Position - 2;
                }
                else
                {
                    LastLineHadBareLf = true;
                    LastContentEnd = Position - 1;
                }

                return true;
            }
        }
    }

    private void Append(ReadOnlySpan<byte> s)
    {
        if (_lineLen + s.Length > _line.Length)
        {
            Array.Resize(ref _line, Math.Max(_line.Length * 2, _lineLen + s.Length));
        }

        s.CopyTo(_line.AsSpan(_lineLen));
        _lineLen += s.Length;
    }

    private bool Fill()
    {
        _bufStart = _bufEnd = 0;
        var n = stream.Read(_buf, 0, _buf.Length);
        if (n <= 0)
        {
            _eof = true;
            return false;
        }

        _bufEnd = n;
        return true;
    }
}
