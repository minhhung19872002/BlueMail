using System.Text;

namespace BlueMail.Mime;

/// <summary>One header field as received: name and raw value bytes (folding kept, without the final CRLF).</summary>
public sealed class HeaderField(string name, byte[] rawValue)
{
    public string Name { get; } = name;

    /// <summary>The value exactly as on the wire after "Name:", folding included.</summary>
    public byte[] RawValue { get; } = rawValue;

    /// <summary>
    /// Unfolded value as text. Raw 8-bit bytes are read as UTF-8 when valid (RFC 6532), otherwise with
    /// <paramref name="fallbackCharset"/> (the message's declared charset) or windows-1252. Encoded-words are not decoded.
    /// </summary>
    public string Value(string? fallbackCharset = null)
    {
        var text = Charsets.IsAscii(RawValue) || Charsets.IsValidUtf8(RawValue)
            ? Encoding.UTF8.GetString(RawValue)
            : (Charsets.GetEncoding(fallbackCharset) ?? Charsets.Latin1Fallback).GetString(RawValue);
        return Unfold(text);
    }

    /// <summary>Unstructured value with encoded-words decoded (Subject, Comments...).</summary>
    public string Text(string? fallbackCharset = null) => EncodedWords.Decode(Value(fallbackCharset)).Trim();

    public bool HasUndeclaredEightBit => !Charsets.IsAscii(RawValue) && !Charsets.IsValidUtf8(RawValue);

    /// <summary>RFC 5322 2.2.3: remove CRLF before folding whitespace.</summary>
    public static string Unfold(string s)
    {
        if (s.IndexOfAny(['\r', '\n']) < 0)
        {
            return s.Trim(' ', '\t');
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is not ('\r' or '\n'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim(' ', '\t');
    }

    public override string ToString() => Name + ": " + Value();
}

/// <summary>Ordered header fields with case-insensitive lookup. Duplicates are kept in order.</summary>
public sealed class HeaderList : List<HeaderField>
{
    public HeaderField? this[string name] => Find(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<HeaderField> All(string name) => this.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? Text(string name, string? fallbackCharset = null) => this[name]?.Text(fallbackCharset);
}

/// <summary>Content-Type or Content-Disposition: a main value and parameters (RFC 2045 5.1, RFC 2183, RFC 2231).</summary>
public sealed class HeaderValueWithParameters
{
    public string Value { get; init; } = "";
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? this[string parameter] => Parameters.TryGetValue(parameter, out var v) ? v : null;

    /// <summary>
    /// Parses "type/subtype; a=b; c*0*=utf-8''%E1...; c*1=...". RFC 2231 continuations are joined in order and
    /// percent-decoded with their charset; RFC 2047 encoded-words inside quoted values (common, non-standard) are
    /// decoded too. Unterminated quotes run to the end of the value instead of failing.
    /// </summary>
    public static HeaderValueWithParameters Parse(string raw, string? fallbackCharset = null)
    {
        var parts = SplitParameters(raw);
        var main = parts.Count > 0 ? parts[0].Trim() : "";
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var continuations = new Dictionary<string, SortedDictionary<int, (string Value, bool Encoded)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = p[..eq].Trim();
            var value = Unquote(p[(eq + 1)..].Trim());
            var encoded = name.EndsWith('*');
            if (encoded)
            {
                name = name[..^1];
            }

            var star = name.IndexOf('*', StringComparison.Ordinal);
            if (star > 0 && int.TryParse(name.AsSpan(star + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
            {
                var baseName = name[..star];
                if (!continuations.TryGetValue(baseName, out var list))
                {
                    continuations[baseName] = list = [];
                }

                list[index] = (value, encoded);
            }
            else if (encoded)
            {
                continuations[name] = new SortedDictionary<int, (string, bool)> { [0] = (value, true) };
            }
            else
            {
                plain.TryAdd(name, value);
            }
        }

        foreach (var (name, segments) in continuations)
        {
            string? charset = null;
            var bytes = new List<byte>();
            foreach (var (i, (value, encoded)) in segments)
            {
                var v = value;
                if (encoded && i == segments.Keys.First())
                {
                    // charset'language'value
                    var q1 = v.IndexOf('\'', StringComparison.Ordinal);
                    var q2 = q1 >= 0 ? v.IndexOf('\'', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1)
                    {
                        charset = v[..q1];
                        v = v[(q2 + 1)..];
                    }
                }

                bytes.AddRange(encoded ? PercentDecode(v) : Encoding.UTF8.GetBytes(v));
            }

            // An extended parameter takes precedence over a plain one with the same name (RFC 2231 4).
            plain[name] = Charsets.Decode(bytes.ToArray(), charset ?? fallbackCharset);
        }

        foreach (var key in plain.Keys.ToList())
        {
            if (plain[key].Contains("=?", StringComparison.Ordinal))
            {
                plain[key] = EncodedWords.Decode(plain[key]);
            }
        }

        return new HeaderValueWithParameters { Value = main.ToLowerInvariant(), Parameters = plain };
    }

    private static List<string> SplitParameters(string raw)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var depth = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < raw.Length)
                {
                    sb.Append(c).Append(raw[++i]);
                    continue;
                }

                if (c == '"')
                {
                    inQuote = false;
                }

                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuote = true;
                    sb.Append(c);
                    break;
                case '(':
                    depth++; // comments are dropped
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ';' when depth == 0:
                    result.Add(sb.ToString());
                    sb.Clear();
                    break;
                default:
                    if (depth == 0)
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    private static string Unquote(string v)
    {
        if (v.Length == 0 || v[0] != '"')
        {
            return v;
        }

        var sb = new StringBuilder(v.Length);
        for (var i = 1; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '\\' && i + 1 < v.Length)
            {
                sb.Append(v[++i]);
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static byte[] PercentDecode(string s)
    {
        var result = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && EncodedWords.Hex(s[i + 1]) is var h and >= 0 && EncodedWords.Hex(s[i + 2]) is var l and >= 0)
            {
                result.Add((byte)((h << 4) | l));
                i += 2;
            }
            else
            {
                result.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }

        return [.. result];
    }
}
