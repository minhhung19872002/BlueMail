using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// RFC 2047 encoded-words: decoding of "=?charset?B|Q?text?=" in unstructured text, phrases and comments,
/// and encoding of non-ASCII text for generated headers.
/// Tolerant decoding (PROMPT 2.6.1): adjacent encoded-words in the same charset are joined at the byte
/// level before charset decoding, so a multi-byte character split across two words decodes correctly;
/// whitespace between adjacent encoded-words is dropped (RFC 2047 6.2); malformed words stay as text.
/// </summary>
public static class EncodedWords
{
    private const int MaxEncodedWordLength = 75;

    /// <summary>Decodes every encoded-word in <paramref name="text"/>.</summary>
    public static string Decode(string text)
    {
        if (!text.Contains("=?", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pendingBytes = new List<byte>();
        string? pendingCharset = null;
        var pendingWhitespace = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (pendingCharset is not null)
            {
                sb.Append(Charsets.Decode(pendingBytes.ToArray(), pendingCharset));
                pendingBytes.Clear();
                pendingCharset = null;
            }
        }

        while (i < text.Length)
        {
            if (TryReadWord(text, i, out var end, out var charset, out var bytes))
            {
                if (pendingCharset is not null && !string.Equals(pendingCharset, charset, StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                }
                else if (pendingCharset is null && pendingWhitespace.Length > 0)
                {
                    sb.Append(pendingWhitespace);
                }

                // Whitespace between two encoded-words is not part of the text.
                pendingWhitespace.Clear();
                pendingCharset = charset;
                pendingBytes.AddRange(bytes);
                i = end;
                continue;
            }

            var c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                pendingWhitespace.Append(c);
                i++;
                continue;
            }

            Flush();
            if (pendingWhitespace.Length > 0)
            {
                sb.Append(pendingWhitespace);
                pendingWhitespace.Clear();
            }

            sb.Append(c);
            i++;
        }

        Flush();
        sb.Append(pendingWhitespace);
        return sb.ToString();
    }

    private static bool TryReadWord(string s, int start, out int end, out string charset, out byte[] bytes)
    {
        end = start;
        charset = "";
        bytes = [];
        if (start + 8 > s.Length || s[start] != '=' || s[start + 1] != '?')
        {
            return false;
        }

        var q1 = s.IndexOf('?', start + 2);
        if (q1 < 0 || q1 + 2 >= s.Length || s[q1 + 2] != '?')
        {
            return false;
        }

        var enc = char.ToUpperInvariant(s[q1 + 1]);
        if (enc is not ('B' or 'Q'))
        {
            return false;
        }

        var close = s.IndexOf("?=", q1 + 3, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        var payload = s.AsSpan(q1 + 3, close - q1 - 3);
        if (payload.IndexOfAny(' ', '\t') >= 0)
        {
            return false;
        }

        charset = s[(start + 2)..q1];
        var star = charset.IndexOf('*', StringComparison.Ordinal); // RFC 2231 language suffix: =?utf-8*vi?B?...?=
        if (star >= 0)
        {
            charset = charset[..star];
        }

        if (charset.Length == 0)
        {
            return false;
        }

        bytes = enc == 'B' ? TransferDecoding.DecodeBase64Lenient(payload) : DecodeQ(payload);
        end = close + 2;
        return true;
    }

    private static byte[] DecodeQ(ReadOnlySpan<char> s)
    {
        var result = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '_')
            {
                result.Add((byte)' ');
            }
            else if (c == '=' && i + 2 < s.Length && Hex(s[i + 1]) is var h and >= 0 && Hex(s[i + 2]) is var l and >= 0)
            {
                result.Add((byte)((h << 4) | l));
                i += 2;
            }
            else
            {
                result.Add(c < 256 ? (byte)c : (byte)'?');
            }
        }

        return [.. result];
    }

    internal static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>
    /// Encodes <paramref name="text"/> for an unstructured header: unchanged when ASCII, otherwise a sequence of
    /// UTF-8 B encoded-words of at most 75 characters, never splitting a character (or grapheme) between words.
    /// </summary>
    public static string Encode(string text)
    {
        if (Ascii.IsValid(text) && !text.Contains("=?", StringComparison.Ordinal))
        {
            return text;
        }

        const string prefix = "=?utf-8?B?";
        const string suffix = "?=";
        var maxPayloadBytes = (MaxEncodedWordLength - prefix.Length - suffix.Length) / 4 * 3; // 45 bytes
        var words = new List<string>();
        var current = new List<byte>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (e.MoveNext())
        {
            var element = Encoding.UTF8.GetBytes(e.GetTextElement());
            if (current.Count + element.Length > maxPayloadBytes && current.Count > 0)
            {
                words.Add(prefix + Convert.ToBase64String([.. current]) + suffix);
                current.Clear();
            }

            current.AddRange(element);
        }

        if (current.Count > 0)
        {
            words.Add(prefix + Convert.ToBase64String([.. current]) + suffix);
        }

        return string.Join(' ', words);
    }
}
