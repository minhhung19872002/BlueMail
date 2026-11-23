using System.Globalization;
using System.Text;

namespace BlueMail.Protocols.Imap;

/// <summary>
/// Modified UTF-7 for mailbox names (RFC 3501 5.1.3): printable ASCII as is except '&amp;' → "&amp;-"; other text as
/// UTF-16 in modified base64 (',' instead of '/') between '&amp;' and '-'. RFC example: "~peter/mail/台北/日本語" ↔
/// "~peter/mail/&amp;U,BTFw-/&amp;ZeVnLIqe-".
/// </summary>
public static class ModifiedUtf7
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+,";

    public static string Encode(string name)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < name.Length)
        {
            var c = name[i];
            if (c is >= (char)0x20 and <= (char)0x7e)
            {
                sb.Append(c == '&' ? "&-" : c.ToString());
                i++;
                continue;
            }

            var start = i;
            while (i < name.Length && name[i] is < (char)0x20 or > (char)0x7e)
            {
                i++;
            }

            var bytes = Encoding.BigEndianUnicode.GetBytes(name[start..i]);
            sb.Append('&');
            var bits = 0;
            var acc = 0;
            foreach (var b in bytes)
            {
                acc = (acc << 8) | b;
                bits += 8;
                while (bits >= 6)
                {
                    bits -= 6;
                    sb.Append(Alphabet[(acc >> bits) & 0x3f]);
                }
            }

            if (bits > 0)
            {
                sb.Append(Alphabet[(acc << (6 - bits)) & 0x3f]);
            }

            sb.Append('-');
        }

        return sb.ToString();
    }

    /// <summary>Decodes a name; invalid sequences are kept as written (a server's name must stay usable as is).</summary>
    public static string Decode(string name)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < name.Length)
        {
            var c = name[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var end = name.IndexOf('-', i + 1);
            if (end < 0)
            {
                sb.Append(name, i, name.Length - i);
                break;
            }

            if (end == i + 1)
            {
                sb.Append('&');
                i = end + 1;
                continue;
            }

            var decoded = DecodeBase64(name.AsSpan(i + 1, end - i - 1));
            sb.Append(decoded ?? name[i..(end + 1)]);
            i = end + 1;
        }

        return sb.ToString();
    }

    private static string? DecodeBase64(ReadOnlySpan<char> s)
    {
        var bytes = new List<byte>();
        var acc = 0;
        var bits = 0;
        foreach (var ch in s)
        {
            var v = Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }

            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((acc >> bits) & 0xff));
            }
        }

        return bytes.Count % 2 == 0 ? Encoding.BigEndianUnicode.GetString([.. bytes]) : null;
    }
}

/// <summary>Writes command arguments: atoms, quoted strings, literals (RFC 3501 4.3, 9).</summary>
public static class ImapArgs
{
    /// <summary>
    /// A string argument as quoted when it can be (printable ASCII); otherwise null and the caller sends a literal
    /// (8-bit or CR/LF content is not allowed in a quoted string).
    /// </summary>
    public static string? TryQuote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is < (char)0x20 or > (char)0x7e)
            {
                return null;
            }

            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    /// <summary>A UID set "1:5,9" from sorted UIDs (RFC 3501 9 sequence-set), shortest form.</summary>
    public static string UidSet(IEnumerable<uint> uids)
    {
        var sorted = uids.Distinct().Order().ToList();
        var sb = new StringBuilder();
        var i = 0;
        while (i < sorted.Count)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(sorted[i].ToString(CultureInfo.InvariantCulture));
            if (j > i)
            {
                sb.Append(':').Append(sorted[j].ToString(CultureInfo.InvariantCulture));
            }

            i = j + 1;
        }

        return sb.ToString();
    }

    /// <summary>INTERNALDATE (RFC 3501 9 date-time): "17-Jul-1996 02:44:25 -0700".</summary>
    public static bool TryParseInternalDate(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(text.Trim(), ["d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out value) || TryParseNumericZone(text.Trim(), out value);
    }

    private static bool TryParseNumericZone(string text, out DateTimeOffset value)
    {
        // .NET "zzz" wants "+07:00"; IMAP writes "+0700".
        value = default;
        var sp = text.LastIndexOf(' ');
        if (sp < 0 || text.Length - sp - 1 != 5)
        {
            return false;
        }

        var zone = text[(sp + 1)..];
        var fixedText = text[..(sp + 1)] + zone[..3] + ":" + zone[3..];
        return DateTimeOffset.TryParseExact(fixedText, ["d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out value);
    }
}
