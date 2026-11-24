using System.Globalization;
using System.Text;

namespace BlueMail.Application.Features.Calendar;

public sealed record CalendarPerson(string? Name, string Address, string? PartStat, string? Role);

/// <summary>One event of an iMIP message, as much as an invitation card and a reply need.</summary>
public sealed record CalendarEvent(
    string Method,
    string Uid,
    int Sequence,
    string? Summary,
    string? Location,
    string? Description,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllDay,
    string? TimeZone,
    string? RecurrenceRule,
    string? RecurrenceId,
    CalendarPerson? Organizer,
    IReadOnlyList<CalendarPerson> Attendees,
    string? Status)
{
    /// <summary>EXDATE values (RFC 5545 3.8.5.1).</summary>
    public IReadOnlyList<DateTimeOffset> ExceptionDates { get; init; } = [];
}

/// <summary>
/// iCalendar (RFC 5545) reading and the iTIP REPLY (RFC 5546 3.2.3) BlueMail sends for an invitation. Input is untrusted:
/// sizes are capped, unknown properties ignored, and a time zone that cannot be resolved falls back to the offset its
/// VTIMEZONE declares (or UTC).
/// </summary>
public static class ICalendar
{
    public const int MaxLength = 1024 * 1024;
    public const int MaxAttendees = 500;

    private sealed record Line(string Name, Dictionary<string, string> Params, string Value);

    /// <summary>The first event (the master, not an override) of a calendar object; null when there is none.</summary>
    public static CalendarEvent? Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        string method = "PUBLISH";
        var stack = new Stack<string>();
        var timeZoneOffsets = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        string? currentTzid = null;
        List<Line>? current = null;
        List<Line>? chosen = null;
        foreach (var line in Unfold(text))
        {
            var l = ParseLine(line);
            if (l is null)
            {
                continue;
            }

            if (l.Name == "BEGIN")
            {
                var comp = l.Value.ToUpperInvariant();
                stack.Push(comp);
                if (comp == "VEVENT" && stack.Count == 2)
                {
                    current = [];
                }

                continue;
            }

            if (l.Name == "END")
            {
                var comp = stack.Count > 0 ? stack.Pop() : "";
                if (comp == "VEVENT" && current is not null)
                {
                    // Prefer the master event; take an override only when there is nothing else.
                    if (chosen is null || (chosen.Any(x => x.Name == "RECURRENCE-ID") && current.All(x => x.Name != "RECURRENCE-ID")))
                    {
                        chosen = current;
                    }

                    current = null;
                }
                else if (comp == "VTIMEZONE")
                {
                    currentTzid = null;
                }

                continue;
            }

            var top = stack.Count > 0 ? stack.Peek() : "";
            if (top == "VCALENDAR" && l.Name == "METHOD")
            {
                method = l.Value.Trim().ToUpperInvariant();
            }
            else if (top == "VEVENT" && current is not null && stack.Count == 2 && current.Count < MaxAttendees + 100)
            {
                current.Add(l);
            }
            else if (top == "VTIMEZONE" && l.Name == "TZID")
            {
                currentTzid = l.Value.Trim();
            }
            else if (top == "STANDARD" && l.Name == "TZOFFSETTO" && currentTzid is not null && TryOffset(l.Value, out var off))
            {
                timeZoneOffsets.TryAdd(currentTzid, off);
            }
        }

        if (chosen is null)
        {
            return null;
        }

        string? Prop(string name) => chosen.FirstOrDefault(x => x.Name == name)?.Value;
        Line? PropLine(string name) => chosen.FirstOrDefault(x => x.Name == name);
        var uid = Prop("UID");
        if (string.IsNullOrWhiteSpace(uid))
        {
            return null;
        }

        var startLine = PropLine("DTSTART");
        var start = startLine is null ? null : ParseTime(startLine, timeZoneOffsets, out _);
        var allDay = startLine is not null && IsDate(startLine);
        var endLine = PropLine("DTEND");
        DateTimeOffset? end = endLine is null ? null : ParseTime(endLine, timeZoneOffsets, out _);
        if (end is null && start is not null)
        {
            end = Prop("DURATION") is { } d && TryDuration(d, out var span) ? start + span : allDay ? start.Value.AddDays(1) : start;
        }

        var tz = startLine?.Params.GetValueOrDefault("TZID");
        var exdates = new List<DateTimeOffset>();
        foreach (var x in chosen.Where(x => x.Name == "EXDATE").Take(500))
        {
            foreach (var v in x.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ParseTime(x with { Value = v }, timeZoneOffsets, out _) is { } t)
                {
                    exdates.Add(t);
                }
            }
        }

        return new CalendarEvent(
            method,
            Cut(uid.Trim(), 255),
            int.TryParse(Prop("SEQUENCE"), NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : 0,
            Text(Prop("SUMMARY"), 500),
            Text(Prop("LOCATION"), 500),
            Text(Prop("DESCRIPTION"), 8000),
            start,
            end,
            allDay,
            tz,
            Prop("RRULE") is { } rr ? Cut(rr, 500) : null,
            Prop("RECURRENCE-ID"),
            PropLine("ORGANIZER") is { } o ? Person(o) : null,
            chosen.Where(x => x.Name == "ATTENDEE").Take(MaxAttendees).Select(Person).Where(p => p.Address.Length > 0).ToList(),
            Prop("STATUS")?.Trim().ToUpperInvariant()) { ExceptionDates = exdates };
    }

    /// <summary>
    /// An iTIP REPLY (RFC 5546 3.2.3) from <paramref name="attendee"/> with <paramref name="partStat"/> (ACCEPTED,
    /// TENTATIVE or DECLINED). Times are written in UTC so no VTIMEZONE is needed.
    /// </summary>
    public static string Reply(CalendarEvent e, CalendarPerson attendee, string partStat, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        void Add(string line) => Fold(sb, line);
        Add("BEGIN:VCALENDAR");
        Add("PRODID:-//BlueMail//BlueMail//EN");
        Add("VERSION:2.0");
        Add("METHOD:REPLY");
        Add("BEGIN:VEVENT");
        Add("UID:" + EscapeText(e.Uid));
        Add("DTSTAMP:" + Utc(now));
        Add("SEQUENCE:" + e.Sequence.ToString(CultureInfo.InvariantCulture));
        if (e.RecurrenceId is { } rid)
        {
            Add("RECURRENCE-ID:" + rid);
        }

        if (e.Organizer is { } o)
        {
            Add("ORGANIZER" + Cn(o.Name) + ":mailto:" + o.Address);
        }

        Add("ATTENDEE;PARTSTAT=" + partStat + Cn(attendee.Name) + ":mailto:" + attendee.Address);
        if (e.Start is { } s)
        {
            Add(e.AllDay ? "DTSTART;VALUE=DATE:" + s.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "DTSTART:" + Utc(s));
        }

        if (e.End is { } en)
        {
            Add(e.AllDay ? "DTEND;VALUE=DATE:" + en.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "DTEND:" + Utc(en));
        }

        if (e.Summary is { } sum)
        {
            Add("SUMMARY:" + EscapeText(sum));
        }

        Add("END:VEVENT");
        Add("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>
    /// An organizer's iTIP REQUEST or CANCEL (RFC 5546 3.2.2 / 3.2.5). A recurring event is written in its own zone
    /// (TZID + VTIMEZONE from the system rules) so every occurrence keeps its wall time; others in UTC.
    /// </summary>
    public static string Organizer(string method, CalendarEvent e, TimeZoneInfo zone, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        void Add(string line) => Fold(sb, line);
        Add("BEGIN:VCALENDAR");
        Add("PRODID:-//BlueMail//BlueMail//EN");
        Add("VERSION:2.0");
        Add("METHOD:" + method);
        var zoned = e.RecurrenceRule is not null && !e.AllDay && zone != TimeZoneInfo.Utc;
        if (zoned && e.Start is { } first)
        {
            foreach (var line in VTimeZone(zone, first))
            {
                Add(line);
            }
        }

        Add("BEGIN:VEVENT");
        Add("UID:" + EscapeText(e.Uid));
        Add("DTSTAMP:" + Utc(now));
        Add("SEQUENCE:" + e.Sequence.ToString(CultureInfo.InvariantCulture));
        if (e.Organizer is { } o)
        {
            Add("ORGANIZER" + Cn(o.Name) + ":mailto:" + o.Address);
        }

        foreach (var a in e.Attendees)
        {
            Add("ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=" + (a.PartStat ?? "NEEDS-ACTION") + ";RSVP=TRUE" + Cn(a.Name) + ":mailto:" + a.Address);
        }

        string Time(string name, DateTimeOffset t) => e.AllDay
            ? name + ";VALUE=DATE:" + t.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : zoned
                ? name + ";TZID=" + zone.Id + ":" + TimeZoneInfo.ConvertTime(t, zone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
                : name + ":" + Utc(t);
        if (e.Start is { } s)
        {
            Add(Time("DTSTART", s));
        }

        if (e.End is { } en)
        {
            Add(Time("DTEND", en));
        }

        if (e.RecurrenceRule is { } rr)
        {
            Add("RRULE:" + rr);
        }

        foreach (var x in e.ExceptionDates)
        {
            Add(Time("EXDATE", x));
        }

        if (e.Summary is { } sum)
        {
            Add("SUMMARY:" + EscapeText(sum));
        }

        if (e.Location is { } loc)
        {
            Add("LOCATION:" + EscapeText(loc));
        }

        if (e.Description is { } desc)
        {
            Add("DESCRIPTION:" + EscapeText(desc));
        }

        Add("STATUS:" + (method == "CANCEL" ? "CANCELLED" : "CONFIRMED"));
        Add("END:VEVENT");
        Add("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>VTIMEZONE (RFC 5545 3.6.5) for a zone from its adjustment rule in force at <paramref name="at"/>.</summary>
    public static IEnumerable<string> VTimeZone(TimeZoneInfo zone, DateTimeOffset at)
    {
        static string Off(TimeSpan t) => (t < TimeSpan.Zero ? "-" : "+") + t.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
        static string Rule(TimeZoneInfo.TransitionTime t) => t.IsFixedDateRule
            ? $"FREQ=YEARLY;BYMONTH={t.Month};BYMONTHDAY={t.Day}"
            : $"FREQ=YEARLY;BYMONTH={t.Month};BYDAY={(t.Week == 5 ? -1 : t.Week)}{t.DayOfWeek.ToString()[..2].ToUpperInvariant()}";
        static string Start(TimeZoneInfo.TransitionTime t) => "19700101T" + t.TimeOfDay.ToString("HHmmss", CultureInfo.InvariantCulture);

        yield return "BEGIN:VTIMEZONE";
        yield return "TZID:" + zone.Id;
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var rule = zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= local && r.DateEnd >= local && r.DaylightDelta != TimeSpan.Zero);
        var standard = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        if (rule is null)
        {
            yield return "BEGIN:STANDARD";
            yield return "DTSTART:19700101T000000";
            yield return "TZOFFSETFROM:" + Off(standard);
            yield return "TZOFFSETTO:" + Off(standard);
            yield return "END:STANDARD";
        }
        else
        {
            var daylight = standard + rule.DaylightDelta;
            yield return "BEGIN:DAYLIGHT";
            yield return "DTSTART:" + Start(rule.DaylightTransitionStart);
            yield return "RRULE:" + Rule(rule.DaylightTransitionStart);
            yield return "TZOFFSETFROM:" + Off(standard);
            yield return "TZOFFSETTO:" + Off(daylight);
            yield return "END:DAYLIGHT";
            yield return "BEGIN:STANDARD";
            yield return "DTSTART:" + Start(rule.DaylightTransitionEnd);
            yield return "RRULE:" + Rule(rule.DaylightTransitionEnd);
            yield return "TZOFFSETFROM:" + Off(daylight);
            yield return "TZOFFSETTO:" + Off(standard);
            yield return "END:STANDARD";
        }

        yield return "END:VTIMEZONE";
    }

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Cn(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "" : ";CN=\"" + name.Replace("\"", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\"";

    public static string EscapeText(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    /// <summary>RFC 5545 3.1: lines longer than 75 octets are folded (never inside a UTF-8 sequence).</summary>
    private static void Fold(StringBuilder sb, string line)
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

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
            }

            sb.Clear().Append(raw.TrimEnd('\r'));
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>name *(";" param) ":" value; parameter values may be quoted and contain ':' ';' ','.</summary>
    private static Line? ParseLine(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is not (';' or ':'))
        {
            i++;
        }

        if (i == 0 || i >= line.Length)
        {
            return null;
        }

        var name = line[..i].Trim().ToUpperInvariant();
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (i < line.Length && line[i] == ';')
        {
            i++;
            var eq = line.IndexOf('=', i);
            if (eq < 0)
            {
                return null;
            }

            var pname = line[i..eq].Trim();
            i = eq + 1;
            var value = new StringBuilder();
            while (i < line.Length && line[i] is not (';' or ':'))
            {
                if (line[i] == '"')
                {
                    var close = line.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    value.Append(line, i + 1, close - i - 1);
                    i = close + 1;
                }
                else
                {
                    value.Append(line[i++]);
                }
            }

            ps.TryAdd(pname, value.ToString());
        }

        return i < line.Length && line[i] == ':' ? new Line(name, ps, line[(i + 1)..]) : null;
    }

    private static CalendarPerson Person(Line l)
    {
        var v = l.Value.Trim();
        var address = v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..] : v.Contains('@', StringComparison.Ordinal) ? v : "";
        address = Uri.UnescapeDataString(address).Trim();
        if (address.Length > 254 || address.Contains(' ', StringComparison.Ordinal))
        {
            address = "";
        }

        return new CalendarPerson(l.Params.GetValueOrDefault("CN") is { Length: > 0 } cn ? Cut(cn, 200) : null, address,
            l.Params.GetValueOrDefault("PARTSTAT")?.ToUpperInvariant(), l.Params.GetValueOrDefault("ROLE")?.ToUpperInvariant());
    }

    private static bool IsDate(Line l) => l.Params.GetValueOrDefault("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true || l.Value.Trim().Length == 8;

    private static DateTimeOffset? ParseTime(Line l, Dictionary<string, TimeSpan> offsets, out bool floating)
    {
        floating = false;
        var v = l.Value.Trim();
        if (IsDate(l))
        {
            return DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new DateTimeOffset(d, TimeSpan.Zero) : null;
        }

        var utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(utc ? v[..^1] : v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        if (utc)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }

        if (l.Params.GetValueOrDefault("TZID") is { } tzid)
        {
            tzid = tzid.Trim().TrimStart('/');
            if (TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
            {
                return new DateTimeOffset(local, zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
            }

            if (offsets.TryGetValue(tzid, out var off))
            {
                return new DateTimeOffset(local, off);
            }
        }

        floating = true; // RFC 5545 3.3.5 floating time: shown as written
        return new DateTimeOffset(local, TimeSpan.Zero);
    }

    private static bool TryOffset(string v, out TimeSpan offset)
    {
        offset = default;
        v = v.Trim();
        if (v.Length is not (5 or 7) || v[0] is not ('+' or '-')
            || !int.TryParse(v.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(v.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || h > 23 || m > 59)
        {
            return false;
        }

        offset = new TimeSpan(h, m, 0) * (v[0] == '-' ? -1 : 1);
        return true;
    }

    /// <summary>RFC 5545 3.3.6 duration: [+-]P(nW | nD[T nH nM nS] | T nH nM nS).</summary>
    public static bool TryDuration(string v, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        v = v.Trim().ToUpperInvariant();
        var sign = 1;
        if (v.StartsWith('-') || v.StartsWith('+'))
        {
            sign = v[0] == '-' ? -1 : 1;
            v = v[1..];
        }

        if (!v.StartsWith('P') || v.Length < 3)
        {
            return false;
        }

        var inTime = false;
        var number = 0L;
        var digits = 0;
        foreach (var c in v[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                number = number * 10 + (c - '0');
                if (++digits > 6)
                {
                    return false;
                }

                continue;
            }

            if (c == 'T')
            {
                inTime = true;
                continue;
            }

            if (digits == 0)
            {
                return false;
            }

            TimeSpan? part = (c, inTime) switch
            {
                ('W', false) => TimeSpan.FromDays(7 * number),
                ('D', false) => TimeSpan.FromDays(number),
                ('H', true) => TimeSpan.FromHours(number),
                ('M', true) => TimeSpan.FromMinutes(number),
                ('S', true) => TimeSpan.FromSeconds(number),
                _ => null,
            };
            if (part is null)
            {
                return false;
            }

            span += part.Value;

            number = 0;
            digits = 0;
        }

        span *= sign;
        return digits == 0;
    }

    private static string? Text(string? v, int max)
    {
        if (v is null)
        {
            return null;
        }

        var sb = new StringBuilder(v.Length);
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                var n = v[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else
            {
                sb.Append(v[i]);
            }
        }

        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : Cut(s, max);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
