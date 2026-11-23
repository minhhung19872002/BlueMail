using System.Globalization;
using System.Text;

namespace BlueMail.Mime;

/// <summary>A mailbox: optional display name and the address (addr-spec).</summary>
public sealed record MailboxAddress(string? Name, string Address)
{
    public override string ToString() => string.IsNullOrEmpty(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>
/// RFC 5322 3.4 address-list parser (plus obsolete syntax of 4.4 and RFC 6532 UTF-8). Groups are flattened
/// into their members ("Team: a@x, b@y;" gives a@x and b@y; an empty group gives nothing). Display names may
/// be quoted, contain comments or RFC 2047 encoded-words (also, non-standard but common, inside quotes).
/// Never throws: unparseable fragments are skipped.
/// </summary>
public static class AddressParser
{
    public static IReadOnlyList<MailboxAddress> Parse(string? value)
    {
        var result = new List<MailboxAddress>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var tokens = Tokenize(value);
        var i = 0;
        while (i < tokens.Count)
        {
            ParseAddress(tokens, ref i, result);
            while (i < tokens.Count && tokens[i].Kind == T.Special && tokens[i].Text is "," or ";")
            {
                i++;
            }
        }

        return result;
    }

    public static MailboxAddress? ParseSingle(string? value) => Parse(value).FirstOrDefault();

    private enum T { Atom, Quoted, Special, Comment, Domain }

    private readonly record struct Token(T Kind, string Text);

    private static void ParseAddress(List<Token> tokens, ref int i, List<MailboxAddress> output)
    {
        // Collect a phrase until '<', ':' (group), '@' (bare addr-spec) or ',' / ';'.
        var phrase = new List<Token>();
        string? lastComment = null;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == T.Comment)
            {
                lastComment = t.Text;
                i++;
                continue;
            }

            if (t.Kind == T.Special && t.Text is "<" or ":" or "," or ";")
            {
                break;
            }

            phrase.Add(t);
            i++;
        }

        if (i < tokens.Count && tokens[i].Text == ":")
        {
            i++; // group: parse members until ';'
            while (i < tokens.Count && tokens[i].Text != ";")
            {
                ParseAddress(tokens, ref i, output);
                if (i < tokens.Count && tokens[i].Text == ",")
                {
                    i++;
                }
            }

            if (i < tokens.Count && tokens[i].Text == ";")
            {
                i++;
            }

            return;
        }

        if (i < tokens.Count && tokens[i].Text == "<")
        {
            i++;
            var addr = new StringBuilder();
            while (i < tokens.Count && tokens[i].Text != ">")
            {
                var t = tokens[i];
                if (t.Kind == T.Special && t.Text == ":" && addr.Length > 0 && addr[0] == '@')
                {
                    addr.Clear(); // obsolete source route "<@a,@b:user@c>"
                }
                else if (t.Kind != T.Comment)
                {
                    addr.Append(t.Kind == T.Quoted ? Quote(t.Text) : t.Text);
                }

                i++;
            }

            if (i < tokens.Count)
            {
                i++; // '>'
            }

            var name = PhraseText(phrase);
            if (addr.Length > 0)
            {
                output.Add(new MailboxAddress(string.IsNullOrEmpty(name) ? null : name, Normalize(addr.ToString())));
            }

            return;
        }

        // Bare addr-spec: tokens of the phrase form the address; a trailing comment is the name ("a@b (Name)").
        var bare = string.Concat(phrase.Select(t => t.Kind == T.Quoted ? Quote(t.Text) : t.Text));
        if (bare.Length > 0)
        {
            output.Add(new MailboxAddress(lastComment is null ? null : EncodedWords.Decode(lastComment).Trim(), Normalize(bare)));
        }
    }

    private static string Normalize(string addr) => addr.Trim();

    private static string Quote(string s) =>
        s.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c) || c > 127) ? s : "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Display name: runs of atoms are joined with spaces and encoded-words decoded across the run.</summary>
    private static string PhraseText(List<Token> phrase)
    {
        var sb = new StringBuilder();
        var atoms = new List<string>();
        void FlushAtoms()
        {
            if (atoms.Count == 0)
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(EncodedWords.Decode(string.Join(' ', atoms)));
            atoms.Clear();
        }

        foreach (var t in phrase)
        {
            if (t.Kind == T.Quoted)
            {
                FlushAtoms();
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(EncodedWords.Decode(t.Text));
            }
            else
            {
                atoms.Add(t.Text);
            }
        }

        FlushAtoms();
        return sb.ToString().Trim();
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                    }

                    sb.Append(s[i]);
                    i++;
                }

                i++; // closing quote (or end of input: unterminated quote tolerated)
                tokens.Add(new Token(T.Quoted, sb.ToString()));
                continue;
            }

            if (c == '(')
            {
                var depth = 1;
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (s[i] == '(')
                    {
                        depth++;
                    }
                    else if (s[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }

                    sb.Append(s[i]);
                    i++;
                }

                tokens.Add(new Token(T.Comment, sb.ToString()));
                continue;
            }

            if (c == '[')
            {
                var end = s.IndexOf(']', i);
                end = end < 0 ? s.Length - 1 : end;
                tokens.Add(new Token(T.Domain, s[i..(end + 1)]));
                i = end + 1;
                continue;
            }

            if ("<>:;,@".Contains(c))
            {
                tokens.Add(c == '@' ? new Token(T.Atom, "@") : new Token(T.Special, c.ToString()));
                i++;
                continue;
            }

            var startAtom = i;
            while (i < s.Length && !" \t\r\n\"()<>:;,@[".Contains(s[i]))
            {
                i++;
            }

            if (i == startAtom)
            {
                // A delimiter with no meaning here (stray ')' or ']'): skip it. Every iteration must consume input.
                i++;
                continue;
            }

            tokens.Add(new Token(T.Atom, s[startAtom..i]));
        }

        // Atoms around '@' and '.' form one address token sequence; join "a" "@" "b" without spaces later.
        return tokens;
    }
}

/// <summary>RFC 5322 3.3 date-time, with the obsolete forms of 4.3 and common real-world deviations.</summary>
public static class DateParser
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    private static readonly Dictionary<string, int> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
        ["EST"] = -500, ["EDT"] = -400, ["CST"] = -600, ["CDT"] = -500,
        ["MST"] = -700, ["MDT"] = -600, ["PST"] = -800, ["PDT"] = -700,
    };

    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Drop comments and the optional day name.
        var s = StripComments(value).Replace(",", " ", StringComparison.Ordinal);
        var parts = s.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Length >= 3 && char.IsLetter(parts[0][0]) && !Months.ContainsKey(parts[0][..3]))
        {
            parts.RemoveAt(0);
        }

        if (parts.Count < 4)
        {
            return null;
        }

        // Accept "5 Nov 2026" and the non-standard "Nov 5 2026".
        int day, month;
        if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out day) && parts[1].Length >= 3 && Months.TryGetValue(parts[1][..3], out month))
        {
        }
        else if (parts[0].Length >= 3 && Months.TryGetValue(parts[0][..3], out month) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out day))
        {
        }
        else
        {
            return null;
        }

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        if (parts[2].Length <= 2)
        {
            year += year < 50 ? 2000 : 1900; // RFC 5322 4.3
        }
        else if (parts[2].Length == 3)
        {
            year += 1900;
        }

        var time = parts[3].Split(':');
        if (time.Length < 2 || !int.TryParse(time[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(time[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        var second = 0;
        if (time.Length > 2 && !int.TryParse(time[2].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out second))
        {
            return null;
        }

        var offsetMinutes = 0;
        if (parts.Count > 4)
        {
            var z = parts[4];
            if ((z[0] is '+' or '-') && z.Length >= 5 && int.TryParse(z.AsSpan(1, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
            {
                offsetMinutes = ((hhmm / 100 * 60) + (hhmm % 100)) * (z[0] == '-' ? -1 : 1);
            }
            else if (Zones.TryGetValue(z, out var named))
            {
                offsetMinutes = (named / 100 * 60) + (named % 100);
            }
        }

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 60 || Math.Abs(offsetMinutes) > 14 * 60)
        {
            return null;
        }

        // The UTC instant must be representable too (0001-01-01 00:00 +1400 is before DateTime.MinValue in UTC).
        var local = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), DateTimeKind.Unspecified);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        if (local.Ticks - offset.Ticks < DateTime.MinValue.Ticks || local.Ticks - offset.Ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new DateTimeOffset(local, offset);
    }

    /// <summary>RFC 5322 date-time for generated headers, e.g. "Mon, 23 Nov 2026 13:05:00 +0700".</summary>
    public static string Format(DateTimeOffset t) =>
        t.ToString("ddd, d MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) +
        (t.Offset < TimeSpan.Zero ? "-" : "+") + t.Offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Message-ID, In-Reply-To, References (RFC 5322 3.6.4): the ids without angle brackets.</summary>
public static class MessageIds
{
    public static IReadOnlyList<string> ParseList(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var i = 0;
        while (i < value.Length)
        {
            var lt = value.IndexOf('<', i);
            if (lt < 0)
            {
                break;
            }

            var gt = value.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                break;
            }

            var id = value[(lt + 1)..gt].Trim();
            if (id.Length > 0 && !id.Contains(' ', StringComparison.Ordinal))
            {
                result.Add(id);
            }

            i = gt + 1;
        }

        if (result.Count == 0)
        {
            // Bracket-less ids seen from broken software: take whitespace-separated tokens containing '@'.
            result.AddRange(value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries).Where(t => t.Contains('@', StringComparison.Ordinal)));
        }

        return result;
    }

    public static string? ParseSingle(string? value) => ParseList(value).FirstOrDefault();
}
