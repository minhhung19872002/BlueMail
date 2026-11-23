using System.Globalization;
using System.Text;

namespace BlueMail.Application.Features.Contacts;

public sealed record LabeledValue(string Value, string? Label);

/// <summary>A contact as exchanged in files (vCard, CSV).</summary>
public sealed record ContactCard
{
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<LabeledValue> Emails { get; init; } = [];
    public IReadOnlyList<LabeledValue> Phones { get; init; } = [];
    public string? Org { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public byte[]? Photo { get; init; }
    public string? PhotoType { get; init; }
}

/// <summary>
/// vCard 4.0 (RFC 6350) writer and a tolerant reader that also takes 3.0 and 2.1 (Outlook, older phones):
/// line unfolding, escapes, TYPE/PREF parameters, QUOTED-PRINTABLE and CHARSET of 2.1, base64 or data: photos.
/// </summary>
public static class VCard
{
    public const int MaxPhotoBytes = 1024 * 1024;

    public static IReadOnlyList<ContactCard> Read(string text)
    {
        var cards = new List<ContactCard>();
        List<(string Name, Dictionary<string, string> Params, string Value)>? props = null;
        foreach (var line in Unfold(text))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var colon = IndexOfUnquoted(line, ':');
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon].Split(';');
            var name = head[0];
            var dot = name.LastIndexOf('.');
            name = (dot >= 0 ? name[(dot + 1)..] : name).ToUpperInvariant(); // drop "item1." groups
            var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in head.Skip(1))
            {
                var eq = p.IndexOf('=', StringComparison.Ordinal);
                var (k, v) = eq < 0 ? ("TYPE", p) : (p[..eq], p[(eq + 1)..].Trim('"'));
                ps[k] = ps.TryGetValue(k, out var prev) ? prev + "," + v : v;
            }

            var value = line[(colon + 1)..];
            if (name == "BEGIN" && value.Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                props = [];
            }
            else if (name == "END" && value.Equals("VCARD", StringComparison.OrdinalIgnoreCase) && props is not null)
            {
                cards.Add(Build(props));
                props = null;
            }
            else
            {
                props?.Add((name, ps, Decode(value, ps)));
            }
        }

        return cards;
    }

    private static string Decode(string value, Dictionary<string, string> ps)
    {
        if (ps.TryGetValue("ENCODING", out var enc) && enc.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = new List<byte>();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
                {
                    bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)value[i]);
                }
            }

            Encoding e;
            try
            {
                e = ps.TryGetValue("CHARSET", out var cs) ? Encoding.GetEncoding(cs) : Encoding.UTF8;
            }
            catch (ArgumentException)
            {
                e = Encoding.UTF8;
            }

            return e.GetString(bytes.ToArray());
        }

        return value;
    }

    private static ContactCard Build(List<(string Name, Dictionary<string, string> Params, string Value)> props)
    {
        string? Text(string n) => props.FirstOrDefault(p => p.Name == n) is { Value: { } v } ? Unescape(v) : null;
        static string? Label(Dictionary<string, string> ps) =>
            ps.TryGetValue("TYPE", out var t)
                ? t.Split(',').Select(x => x.Trim().ToLowerInvariant()).FirstOrDefault(x => x is not ("pref" or "internet" or "voice" or "x400" or ""))
                : null;

        var fn = Text("FN");
        if (string.IsNullOrWhiteSpace(fn) && Text("N") is { } n)
        {
            // N: family;given;additional;prefix;suffix
            var parts = SplitUnescaped(n, ';');
            fn = string.Join(' ', new[] { parts.ElementAtOrDefault(3), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(0) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        var emails = props.Where(p => p.Name == "EMAIL").Select(p => new LabeledValue(Unescape(p.Value).Trim(), Label(p.Params)))
            .Where(e => e.Value.Contains('@', StringComparison.Ordinal)).ToList();
        var phones = props.Where(p => p.Name == "TEL")
            .Select(p => new LabeledValue(Unescape(p.Value).Replace("tel:", "", StringComparison.OrdinalIgnoreCase).Trim(), Label(p.Params)))
            .Where(t => t.Value.Length > 0).ToList();
        byte[]? photo = null;
        string? photoType = null;
        if (props.FirstOrDefault(p => p.Name == "PHOTO") is { Value: { } pv } photoProp)
        {
            try
            {
                if (pv.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && pv.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
                {
                    photoType = pv[5..pv.IndexOf(';', StringComparison.Ordinal)].ToLowerInvariant();
                    photo = Convert.FromBase64String(pv[(pv.IndexOf(',', StringComparison.Ordinal) + 1)..]);
                }
                else if (photoProp.Params.TryGetValue("ENCODING", out var e) && e is "b" or "B" or "BASE64" or "base64")
                {
                    photo = Convert.FromBase64String(pv);
                    photoType = photoProp.Params.TryGetValue("TYPE", out var t) && t.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                }
            }
            catch (FormatException)
            {
                photo = null;
            }

            if (photo is { Length: > MaxPhotoBytes } || photoType is not ("image/jpeg" or "image/png"))
            {
                (photo, photoType) = (null, null);
            }
        }

        var org = Text("ORG") is { } o ? SplitUnescaped(o, ';')[0] : null;
        return new ContactCard
        {
            DisplayName = (fn ?? emails.FirstOrDefault()?.Value ?? "").Trim(),
            Emails = emails,
            Phones = phones,
            Org = string.IsNullOrWhiteSpace(org) ? null : org,
            Title = Text("TITLE"),
            Notes = Text("NOTE"),
            Photo = photo,
            PhotoType = photoType,
        };
    }

    public static string Write(IEnumerable<ContactCard> cards)
    {
        var sb = new StringBuilder();
        foreach (var c in cards)
        {
            Line(sb, "BEGIN:VCARD");
            Line(sb, "VERSION:4.0");
            Line(sb, "FN:" + Escape(c.DisplayName));
            Line(sb, "N:" + Escape(c.DisplayName) + ";;;;");
            foreach (var e in c.Emails)
            {
                Line(sb, "EMAIL" + (e.Label is null ? "" : ";TYPE=" + Param(e.Label)) + ":" + Escape(e.Value));
            }

            foreach (var t in c.Phones)
            {
                Line(sb, "TEL;VALUE=uri" + (t.Label is null ? "" : ";TYPE=" + Param(t.Label)) + ":tel:" + Escape(t.Value.Replace(" ", "", StringComparison.Ordinal)));
            }

            if (c.Org is not null)
            {
                Line(sb, "ORG:" + Escape(c.Org));
            }

            if (c.Title is not null)
            {
                Line(sb, "TITLE:" + Escape(c.Title));
            }

            if (c.Notes is not null)
            {
                Line(sb, "NOTE:" + Escape(c.Notes));
            }

            if (c.Photo is not null && c.PhotoType is not null)
            {
                Line(sb, "PHOTO:data:" + c.PhotoType + ";base64," + Convert.ToBase64String(c.Photo));
            }

            Line(sb, "END:VCARD");
        }

        return sb.ToString();
    }

    private static string Param(string v) => new(v.Where(ch => char.IsLetterOrDigit(ch) || ch is '-').ToArray());

    /// <summary>Content lines folded at 75 octets without splitting a UTF-8 sequence (RFC 6350 3.2).</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var bytes = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > (first ? 75 : 74))
            {
                sb.Append("\r\n ");
                bytes = 0;
                first = false;
            }

            sb.Append(rune.ToString());
            bytes += n;
        }

        sb.Append("\r\n");
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var c = s[++i];
                sb.Append(c is 'n' or 'N' ? '\n' : c);
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitUnescaped(string s, char sep)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[++i]);
            }
            else if (s[i] == sep)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        parts.Add(sb.ToString());
        return parts;
    }

    private static int IndexOfUnquoted(string s, char c)
    {
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                quoted = !quoted;
            }
            else if (s[i] == c && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var current = new StringBuilder();
        var qpSoftBreak = false;
        foreach (var raw in lines)
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && current.Length > 0)
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (qpSoftBreak)
            {
                // vCard 2.1 quoted-printable continues with "=" at the end of a line
                current.Length--;
                current.Append(raw);
                qpSoftBreak = raw.EndsWith('=');
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(raw);
            qpSoftBreak = raw.EndsWith('=') && raw.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}

/// <summary>CSV (RFC 4180) in the layouts of Google Contacts and Outlook; export uses the Google layout.</summary>
public static class ContactCsv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(f => f.Length > 0))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    public static IReadOnlyList<ContactCard> Read(string text)
    {
        var rows = Parse(text.TrimStart('﻿'));
        if (rows.Count < 2)
        {
            return [];
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        string? Col(IReadOnlyList<string> r, params string[] names)
        {
            foreach (var n in names)
            {
                var i = header.FindIndex(h => h.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i < r.Count && !string.IsNullOrWhiteSpace(r[i]))
                {
                    return r[i].Trim();
                }
            }

            return null;
        }

        var cards = new List<ContactCard>();
        foreach (var r in rows.Skip(1))
        {
            var emails = new List<LabeledValue>();
            var phones = new List<LabeledValue>();
            for (var n = 1; n <= 5; n++)
            {
                // Google: "E-mail 1 - Value" may hold several addresses separated by " ::: "
                foreach (var v in (Col(r, $"E-mail {n} - Value") ?? "").Split(":::", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    emails.Add(new LabeledValue(v, Col(r, $"E-mail {n} - Type")?.Trim('*', ' ').ToLowerInvariant()));
                }

                foreach (var v in (Col(r, $"Phone {n} - Value") ?? "").Split(":::", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    phones.Add(new LabeledValue(v, Col(r, $"Phone {n} - Type")?.Trim('*', ' ').ToLowerInvariant()));
                }
            }

            // Outlook
            foreach (var (name, label) in new[] { ("E-mail Address", (string?)null), ("E-mail 2 Address", null), ("E-mail 3 Address", null) })
            {
                if (Col(r, name) is { } v)
                {
                    emails.Add(new LabeledValue(v, label));
                }
            }

            foreach (var (name, label) in new[] { ("Mobile Phone", "mobile"), ("Business Phone", "work"), ("Home Phone", "home"), ("Primary Phone", (string?)null) })
            {
                if (Col(r, name) is { } v)
                {
                    phones.Add(new LabeledValue(v, label));
                }
            }

            var name0 = Col(r, "Name", "Display Name") ?? string.Join(' ', new[] { Col(r, "First Name", "Given Name"), Col(r, "Middle Name", "Additional Name"),
                Col(r, "Last Name", "Family Name") }.Where(x => x is not null));
            emails = emails.Where(e => e.Value.Contains('@', StringComparison.Ordinal)).DistinctBy(e => e.Value.ToLowerInvariant()).ToList();
            if (string.IsNullOrWhiteSpace(name0) && emails.Count == 0)
            {
                continue;
            }

            cards.Add(new ContactCard
            {
                DisplayName = string.IsNullOrWhiteSpace(name0) ? emails[0].Value : name0,
                Emails = emails,
                Phones = phones,
                Org = Col(r, "Organization 1 - Name", "Organization Name", "Company"),
                Title = Col(r, "Organization 1 - Title", "Organization Title", "Job Title"),
                Notes = Col(r, "Notes"),
            });
        }

        return cards;
    }

    public static string Write(IEnumerable<ContactCard> cards)
    {
        var sb = new StringBuilder();
        string[] header = ["Name", "E-mail 1 - Type", "E-mail 1 - Value", "E-mail 2 - Type", "E-mail 2 - Value", "E-mail 3 - Type", "E-mail 3 - Value",
            "Phone 1 - Type", "Phone 1 - Value", "Phone 2 - Type", "Phone 2 - Value", "Organization Name", "Organization Title", "Notes"];
        sb.Append(string.Join(',', header)).Append("\r\n");
        foreach (var c in cards)
        {
            var cells = new List<string?> { c.DisplayName };
            for (var i = 0; i < 3; i++)
            {
                cells.Add(c.Emails.ElementAtOrDefault(i)?.Label);
                cells.Add(c.Emails.ElementAtOrDefault(i)?.Value);
            }

            for (var i = 0; i < 2; i++)
            {
                cells.Add(c.Phones.ElementAtOrDefault(i)?.Label);
                cells.Add(c.Phones.ElementAtOrDefault(i)?.Value);
            }

            cells.AddRange([c.Org, c.Title, c.Notes]);
            sb.Append(string.Join(',', cells.Select(Quote))).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Quotes as RFC 4180; a leading = + - @ is prefixed so spreadsheets do not run it as a formula.</summary>
    private static string Quote(string? v)
    {
        if (string.IsNullOrEmpty(v))
        {
            return "";
        }

        // "+84 912…" stays a phone number; "=HYPERLINK(…)" or "+cmd" does not stay a formula.
        if (v[0] is '=' or '@' || (v[0] is '+' or '-' && (v.Length == 1 || !char.IsAsciiDigit(v[1]))))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
