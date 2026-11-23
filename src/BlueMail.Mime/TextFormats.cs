using System.Net;
using System.Text;

namespace BlueMail.Mime;

/// <summary>RFC 3676 format=flowed: joins soft-broken lines (ending in a space), keeps quote depth, handles space-stuffing.</summary>
public static class FormatFlowed
{
    public static string Decode(string text, bool delSp)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        var paragraph = new StringBuilder();
        var currentDepth = -1;

        void EndParagraph()
        {
            if (currentDepth < 0)
            {
                return;
            }

            sb.Append(new string('>', currentDepth));
            if (currentDepth > 0 && paragraph.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(paragraph).Append('\n');
            paragraph.Clear();
            currentDepth = -1;
        }

        foreach (var raw in lines)
        {
            var depth = 0;
            while (depth < raw.Length && raw[depth] == '>')
            {
                depth++;
            }

            var line = raw[depth..];
            if (line.StartsWith(' '))
            {
                line = line[1..]; // space-stuffing (4.4)
            }

            var isSignature = line == "-- ";
            var flowed = !isSignature && line.EndsWith(' ');
            if (currentDepth >= 0 && depth != currentDepth)
            {
                EndParagraph(); // quote depth changed: the paragraph ends (4.5)
            }

            currentDepth = depth;
            paragraph.Append(flowed && delSp ? line[..^1] : line);
            if (!flowed)
            {
                EndParagraph();
            }
        }

        EndParagraph();
        var result = sb.ToString();
        return text.EndsWith('\n') ? result : result.TrimEnd('\n');
    }
}

/// <summary>
/// Plain text from HTML for snippets, the search index and the text/plain alternative when composing.
/// A small tag stripper, not a renderer: drops script/style/head, turns block elements into line breaks,
/// decodes entities, collapses whitespace. Safe HTML for display uses HtmlSanitizer, not this.
/// </summary>
public static class HtmlToText
{
    private static readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "table", "ul", "ol", "hr", "pre", "section", "article", "header", "footer",
    };

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "head", "title", "noscript", "template" };

    public static string Convert(string html, int maxChars = 1_000_000)
    {
        var sb = new StringBuilder(Math.Min(html.Length, maxChars));
        var i = 0;
        string? skipping = null;
        while (i < html.Length && sb.Length < maxChars)
        {
            var c = html[i];
            if (c == '<')
            {
                if (html.AsSpan(i).StartsWith("<!--"))
                {
                    var endComment = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = endComment < 0 ? html.Length : endComment + 3;
                    continue;
                }

                var end = html.IndexOf('>', i + 1);
                if (end < 0)
                {
                    break;
                }

                var tag = html[(i + 1)..end].Trim();
                var closing = tag.StartsWith('/');
                var name = new string(tag.TrimStart('/').TakeWhile(ch => char.IsAsciiLetterOrDigit(ch)).ToArray());
                if (skipping is not null)
                {
                    if (closing && string.Equals(name, skipping, StringComparison.OrdinalIgnoreCase))
                    {
                        skipping = null;
                    }
                }
                else if (!closing && Skip.Contains(name) && !tag.EndsWith('/'))
                {
                    skipping = name;
                }
                else if (Block.Contains(name))
                {
                    sb.Append('\n');
                }
                else if (string.Equals(name, "td", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "th", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(' ');
                }

                i = end + 1;
                continue;
            }

            if (skipping is null)
            {
                sb.Append(c);
            }

            i++;
        }

        var decoded = WebUtility.HtmlDecode(sb.ToString());
        return Collapse(decoded);
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        var newlines = 0;
        foreach (var raw in s)
        {
            var c = raw == ' ' ? ' ' : raw;
            if (c == '\n')
            {
                if (newlines < 2 && sb.Length > 0)
                {
                    while (sb.Length > 0 && sb[^1] == ' ')
                    {
                        sb.Length--;
                    }

                    sb.Append('\n');
                }

                newlines++;
                lastSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            sb.Append(c);
            lastSpace = false;
            newlines = 0;
        }

        return sb.ToString().Trim();
    }
}

/// <summary>Just enough of iCalendar (RFC 5545) to show an invitation card (RFC 6047 iMIP).</summary>
public sealed record CalendarInvite(string? Method, string? Uid, string? Summary, string? Location,
    DateTimeOffset? Start, DateTimeOffset? End, string? Organizer, IReadOnlyList<string> Attendees, int Sequence)
{
    public static CalendarInvite? Parse(string ics)
    {
        var lines = Unfold(ics);
        string? method = null, uid = null, summary = null, location = null, organizer = null;
        DateTimeOffset? start = null, end = null;
        var attendees = new List<string>();
        var sequence = 0;
        var inEvent = false;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var nameAndParams = line[..colon];
            var value = line[(colon + 1)..];
            var name = nameAndParams.Split(';')[0].ToUpperInvariant();
            if (name == "BEGIN" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                continue;
            }

            if (name == "END" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                break; // first event only
            }

            if (name == "METHOD")
            {
                method = value.Trim().ToUpperInvariant();
            }

            if (!inEvent)
            {
                continue;
            }

            switch (name)
            {
                case "UID": uid = value; break;
                case "SUMMARY": summary = Unescape(value); break;
                case "LOCATION": location = Unescape(value); break;
                case "DTSTART": start = ParseDate(value, nameAndParams); break;
                case "DTEND": end = ParseDate(value, nameAndParams); break;
                case "ORGANIZER": organizer = Mailto(value); break;
                case "ATTENDEE": attendees.Add(Mailto(value)); break;
                case "SEQUENCE": _ = int.TryParse(value, out sequence); break;
            }
        }

        return uid is null && summary is null ? null : new CalendarInvite(method, uid, summary, location, start, end, organizer, attendees, sequence);
    }

    private static string Mailto(string v) => v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..] : v;

    private static string Unescape(string v) =>
        v.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal)
         .Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    private static DateTimeOffset? ParseDate(string v, string nameAndParams)
    {
        var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd" };
        var utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return null;
        }

        if (utc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        var tzid = nameAndParams.Split(';').FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"');
        if (tzid is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var tz))
        {
            return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
        }

        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)); // floating time: shown as written
    }

    private static List<string> Unfold(string ics)
    {
        var result = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t' && result.Count > 0)
            {
                result[^1] += raw[1..];
            }
            else
            {
                result.Add(raw);
            }
        }

        return result;
    }
}

/// <summary>A per-recipient block of message/delivery-status (RFC 3464 2.3).</summary>
public sealed record DeliveryStatusRecipient(string? FinalRecipient, string? Action, string? Status, string? DiagnosticCode, string? RemoteMta);

public static class DeliveryStatus
{
    /// <summary>Parses the body of a message/delivery-status part.</summary>
    public static IReadOnlyList<DeliveryStatusRecipient> Parse(string body)
    {
        var blocks = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<DeliveryStatusRecipient>();
        foreach (var block in blocks.Skip(1)) // first block is per-message
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? last = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.Length > 0 && line[0] is ' ' or '\t' && last is not null)
                {
                    fields[last] += " " + line.Trim();
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    last = line[..colon].Trim();
                    fields[last] = line[(colon + 1)..].Trim();
                }
            }

            string? Get(string k) => fields.TryGetValue(k, out var v) ? v : null;
            static string? AfterType(string? v) => v is null ? null : v.Contains(';', StringComparison.Ordinal) ? v[(v.IndexOf(';', StringComparison.Ordinal) + 1)..].Trim() : v;
            if (fields.Count > 0)
            {
                result.Add(new DeliveryStatusRecipient(AfterType(Get("Final-Recipient")), Get("Action")?.ToLowerInvariant(), Get("Status"),
                    AfterType(Get("Diagnostic-Code")), AfterType(Get("Remote-MTA"))));
            }
        }

        return result;
    }
}
