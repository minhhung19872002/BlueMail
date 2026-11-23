using System.Text;

namespace BlueMail.Protocols.Imap;

/// <summary>
/// Tokeniser over an <see cref="ImapLine"/> (RFC 3501 section 9 grammar, shared by client and server).
/// Atoms keep section brackets whole, so <c>BODY[HEADER.FIELDS (FROM TO)]&lt;0&gt;</c> is one atom.
/// </summary>
public sealed class ImapParser
{
    private readonly ImapLine _line;
    private int _segment;
    private int _pos;

    public ImapParser(ImapLine line) => _line = line;

    private byte[] Current => _line.Text[_segment];

    public bool AtEnd => _segment >= _line.Text.Count - 1 && _pos >= Current.Length;

    /// <summary>Peeks the next byte of the current text part, or -1 at its end.</summary>
    public int Peek() => _pos < Current.Length ? Current[_pos] : -1;

    /// <summary>Position in the current text part, to read a word again as values.</summary>
    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    public bool TrySkip(byte b)
    {
        if (Peek() != b)
        {
            return false;
        }

        _pos++;
        return true;
    }

    public void SkipSpaces()
    {
        while (Peek() == ' ')
        {
            _pos++;
        }
    }

    /// <summary>The unparsed rest of the current text part (the human text of a status response).</summary>
    public string Rest()
    {
        var s = Encoding.UTF8.GetString(Current, _pos, Current.Length - _pos);
        _pos = Current.Length;
        return s;
    }

    /// <summary>Reads characters up to a space or the end; for tags and command names.</summary>
    public string ReadWord()
    {
        var start = _pos;
        while (_pos < Current.Length && Current[_pos] != ' ')
        {
            _pos++;
        }

        return Encoding.ASCII.GetString(Current, start, _pos - start);
    }

    /// <summary>Reads all values to the end of the line.</summary>
    public List<ImapValue> ReadAll()
    {
        var list = new List<ImapValue>();
        while (true)
        {
            SkipSpaces();
            if (AtEnd)
            {
                return list;
            }

            list.Add(ReadValue(stopAtBracket: false));
        }
    }

    public ImapValue ReadValue(bool stopAtBracket)
    {
        SkipSpaces();
        var c = Peek();
        switch (c)
        {
            case -1:
                throw new ImapProtocolException("Value expected.");
            case '(':
                {
                    _pos++;
                    var items = new List<ImapValue>();
                    while (true)
                    {
                        SkipSpaces();
                        if (TrySkip((byte)')'))
                        {
                            return new ImapList(items);
                        }

                        if (Peek() == -1)
                        {
                            throw new ImapProtocolException("Unclosed list.");
                        }

                        items.Add(ReadValue(stopAtBracket));
                    }
                }

            case '"':
                return new ImapString(ReadQuoted());
            case '{':
                return ReadLiteral();
            case '~' when _pos + 1 < Current.Length && Current[_pos + 1] == '{':
                _pos++;
                return ReadLiteral();
            default:
                {
                    var atom = ReadAtom(stopAtBracket);
                    if (atom.Length == 0)
                    {
                        throw new ImapProtocolException($"Unexpected character '{(char)c}'.");
                    }

                    if (string.Equals(atom, "NIL", StringComparison.OrdinalIgnoreCase))
                    {
                        return ImapNil.Instance;
                    }

                    return atom.Length <= 18 && atom.All(char.IsAsciiDigit) ? new ImapNumber(long.Parse(atom, System.Globalization.CultureInfo.InvariantCulture)) : new ImapAtom(atom);
                }
        }
    }

    /// <summary>
    /// Atom characters (RFC 3501 ATOM-CHAR plus '\' for flags and ']' outside response codes); a '[' opens a section that
    /// may contain spaces and parentheses until its matching ']'.
    /// </summary>
    public string ReadAtom(bool stopAtBracket)
    {
        var start = _pos;
        var depth = 0;
        var b = Current;
        while (_pos < b.Length)
        {
            var c = b[_pos];
            if (depth > 0)
            {
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                }

                _pos++;
                continue;
            }

            if (c is (byte)' ' or (byte)'(' or (byte)')' or (byte)'"' or (byte)'{' || c < 0x20 || c == 0x7f || (stopAtBracket && c == ']'))
            {
                break;
            }

            if (c == '[')
            {
                depth++;
            }

            _pos++;
        }

        return Encoding.UTF8.GetString(b, start, _pos - start);
    }

    private byte[] ReadQuoted()
    {
        _pos++; // opening quote
        var b = Current;
        using var ms = new MemoryStream();
        while (_pos < b.Length)
        {
            var c = b[_pos++];
            if (c == '"')
            {
                return ms.ToArray();
            }

            if (c == '\\' && _pos < b.Length)
            {
                c = b[_pos++];
            }

            ms.WriteByte(c);
        }

        throw new ImapProtocolException("Unterminated quoted string.");
    }

    private ImapString ReadLiteral()
    {
        // "{n}" / "{n+}" ends this text part; the literal bytes are the next item, then parsing goes on in the next part.
        if (!ImapReader.LiteralAtEnd(Current.AsSpan(_pos), out _, out _) || _segment >= _line.Literals.Count)
        {
            throw new ImapProtocolException("Literal marker not at the end of the line.");
        }

        var data = _line.Literals[_segment];
        _segment++;
        _pos = 0;
        return new ImapString(data);
    }
}

/// <summary>Builds <see cref="ImapResponse"/> values from server lines (RFC 3501 7, 9 "response").</summary>
public static class ImapResponseParser
{
    public static ImapResponse Parse(ImapLine line)
    {
        var p = new ImapParser(line);
        var tag = p.ReadWord();
        if (tag.Length == 0)
        {
            throw new ImapProtocolException("Empty response.");
        }

        p.SkipSpaces();
        if (tag == "+")
        {
            return new ImapResponse("+", null, null, [], p.Rest(), []);
        }

        // Status words of tagged and untagged responses; anything else after "*" is data, parsed as values from the start.
        var mark = p.Position;
        var word = p.ReadWord();
        ImapStatus? status = word.ToUpperInvariant() switch
        {
            "OK" => ImapStatus.Ok,
            "NO" => ImapStatus.No,
            "BAD" => ImapStatus.Bad,
            "BYE" => ImapStatus.Bye,
            "PREAUTH" => ImapStatus.PreAuth,
            _ => null,
        };

        if (status is null)
        {
            if (tag != "*")
            {
                throw new ImapProtocolException($"Tagged response without status: {line}");
            }

            p.Position = mark;
            return new ImapResponse(tag, null, null, [], "", p.ReadAll());
        }

        p.SkipSpaces();
        string? code = null;
        var args = new List<ImapValue>();
        if (p.TrySkip((byte)'['))
        {
            code = p.ReadAtom(stopAtBracket: true).ToUpperInvariant();
            while (true)
            {
                p.SkipSpaces();
                if (p.TrySkip((byte)']'))
                {
                    break;
                }

                if (p.Peek() == -1)
                {
                    throw new ImapProtocolException("Unclosed response code.");
                }

                args.Add(p.ReadValue(stopAtBracket: true));
            }

            p.SkipSpaces();
        }

        return new ImapResponse(tag, status, code, args, p.Rest(), []);
    }
}
