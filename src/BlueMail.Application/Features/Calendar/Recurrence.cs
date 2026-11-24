using System.Globalization;

namespace BlueMail.Application.Features.Calendar;

/// <summary>A parsed RRULE (RFC 5545 3.3.10), the subset calendars send in practice.</summary>
public sealed record RecurrenceRule(
    string Freq,
    int Interval,
    int? Count,
    DateTimeOffset? Until,
    IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDay,
    IReadOnlyList<int> ByMonthDay,
    IReadOnlyList<int> ByMonth)
{
    public static readonly string[] Frequencies = ["DAILY", "WEEKLY", "MONTHLY", "YEARLY"];

    /// <summary>Null for rules we cannot expand (other frequencies, BYSETPOS, BYHOUR…): such events show their first occurrence only.</summary>
    public static RecurrenceRule? Parse(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule) || rule.Length > 500)
        {
            return null;
        }

        string? freq = null;
        int interval = 1;
        int? count = null;
        DateTimeOffset? until = null;
        var byDay = new List<(int, DayOfWeek)>();
        var byMonthDay = new List<int>();
        var byMonth = new List<int>();
        foreach (var part in rule.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return null;
            }

            var name = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim().ToUpperInvariant();
            switch (name)
            {
                case "FREQ":
                    freq = Frequencies.Contains(value) ? value : null;
                    if (freq is null)
                    {
                        return null;
                    }

                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval is < 1 or > 1000)
                    {
                        return null;
                    }

                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var c) || c is < 1 or > 10_000)
                    {
                        return null;
                    }

                    count = c;
                    break;
                case "UNTIL":
                    until = ParseUntil(value);
                    if (until is null)
                    {
                        return null;
                    }

                    break;
                case "BYDAY":
                    foreach (var d in value.Split(','))
                    {
                        if (ParseDay(d) is not { } day)
                        {
                            return null;
                        }

                        byDay.Add(day);
                    }

                    break;
                case "BYMONTHDAY":
                    foreach (var d in value.Split(','))
                    {
                        if (!int.TryParse(d, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n == 0 || n is < -31 or > 31)
                        {
                            return null;
                        }

                        byMonthDay.Add(n);
                    }

                    break;
                case "BYMONTH":
                    foreach (var d in value.Split(','))
                    {
                        if (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 12)
                        {
                            return null;
                        }

                        byMonth.Add(n);
                    }

                    break;
                case "WKST":
                    break;
                default:
                    return null; // BYSETPOS, BYHOUR, BYWEEKNO…: not expanded
            }
        }

        return freq is null ? null : new RecurrenceRule(freq, interval, count, until, byDay, byMonthDay, byMonth);
    }

    private static DateTimeOffset? ParseUntil(string v)
    {
        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return new DateTimeOffset(d.AddDays(1).AddTicks(-1), TimeSpan.Zero); // the whole day counts
        }

        var utc = v.EndsWith('Z');
        return DateTime.TryParseExact(utc ? v[..^1] : v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero) : null;
    }

    private static (int, DayOfWeek)? ParseDay(string d)
    {
        d = d.Trim();
        if (d.Length < 2)
        {
            return null;
        }

        DayOfWeek? day = d[^2..] switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
        };
        if (day is null)
        {
            return null;
        }

        var ord = 0;
        if (d.Length > 2 && (!int.TryParse(d[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ord) || ord == 0 || ord is < -5 or > 5))
        {
            return null;
        }

        return (ord, day.Value);
    }
}

/// <summary>
/// Occurrences of a recurring event in a window. Dates are stepped in the event's own wall time (<paramref name="zone"/>),
/// so a 09:00 weekly meeting stays at 09:00 across daylight-saving changes. Expansion is capped (<see cref="MaxOccurrences"/>).
/// </summary>
public static class Recurrence
{
    public const int MaxOccurrences = 1000;

    /// <summary>Start times of the occurrences that overlap [from, to); <paramref name="duration"/> is the event length.</summary>
    public static IReadOnlyList<DateTimeOffset> Expand(DateTimeOffset start, TimeSpan duration, RecurrenceRule? rule, TimeZoneInfo zone,
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<DateTimeOffset>? exceptions = null)
    {
        var result = new List<DateTimeOffset>();
        if (rule is null)
        {
            if (start < to && start + duration > from)
            {
                result.Add(start);
            }

            return result;
        }

        var local = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var produced = 0;
        var steps = 0;
        foreach (var candidate in Candidates(local, rule))
        {
            if (++steps > 200_000)
            {
                break;
            }

            if (candidate < local)
            {
                continue; // RFC 5545: the rule never produces instances before DTSTART
            }

            var at = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
            if (rule.Until is { } until && at > until)
            {
                break;
            }

            produced++;
            if (rule.Count is { } count && produced > count)
            {
                break;
            }

            if (at >= to)
            {
                break;
            }

            if (at + duration > from && (exceptions is null || !exceptions.Contains(at)))
            {
                result.Add(at);
                if (result.Count >= MaxOccurrences)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Candidate local start times in order, period by period.</summary>
    private static IEnumerable<DateTime> Candidates(DateTime start, RecurrenceRule r)
    {
        var time = start.TimeOfDay;
        // Periods that stay before year 9999 (DateTime's range).
        var span = (long)r.Interval;
        var maxPeriods = r.Freq switch
        {
            "DAILY" => (9998L - start.Year) * 365 / span,
            "WEEKLY" => (9998L - start.Year) * 52 / span,
            "MONTHLY" => (9998L - start.Year) * 12 / span,
            _ => (9998L - start.Year) / span,
        };
        for (var period = 0; period < Math.Min(100_000, maxPeriods); period++)
        {
            IEnumerable<DateTime> days;
            switch (r.Freq)
            {
                case "DAILY":
                {
                    var day = start.Date.AddDays((double)period * r.Interval);
                    days = Filter([day], r);
                    break;
                }

                case "WEEKLY":
                {
                    // Weeks start on Monday (WKST default); BYDAY picks days in the week, else the start's weekday.
                    var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(7.0 * period * r.Interval);
                    var wanted = r.ByDay.Count > 0 ? r.ByDay.Select(b => b.Day).ToHashSet() : [start.DayOfWeek];
                    days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Where(d => wanted.Contains(d.DayOfWeek));
                    days = Filter(days, r with { ByDay = [] });
                    break;
                }

                case "MONTHLY":
                {
                    var month = new DateTime(start.Year, start.Month, 1).AddMonths(period * r.Interval);
                    days = MonthDays(month, r, start);
                    break;
                }

                default: // YEARLY
                {
                    var year = start.Year + period * r.Interval;

                    var months = r.ByMonth.Count > 0 ? r.ByMonth : [start.Month];
                    days = months.Order().SelectMany(m => MonthDays(new DateTime(year, m, 1), r with { ByMonth = [] }, start));
                    break;
                }
            }

            foreach (var d in days.Distinct().Order())
            {
                yield return d + time;
            }
        }
    }

    private static IEnumerable<DateTime> MonthDays(DateTime month, RecurrenceRule r, DateTime start)
    {
        var length = DateTime.DaysInMonth(month.Year, month.Month);
        var all = Enumerable.Range(0, length).Select(i => month.AddDays(i)).ToList();
        IEnumerable<DateTime> days;
        if (r.ByMonthDay.Count > 0)
        {
            days = r.ByMonthDay.Select(n => n > 0 ? n : length + n + 1).Where(n => n >= 1 && n <= length).Select(n => month.AddDays(n - 1));
        }
        else if (r.ByDay.Count > 0)
        {
            days = r.ByDay.SelectMany(b =>
            {
                var matching = all.Where(d => d.DayOfWeek == b.Day).ToList();
                if (b.Ordinal == 0)
                {
                    return matching;
                }

                var i = b.Ordinal > 0 ? b.Ordinal - 1 : matching.Count + b.Ordinal;
                return i >= 0 && i < matching.Count ? [matching[i]] : Enumerable.Empty<DateTime>();
            });
        }
        else
        {
            // Same day of month as the start; months without it are skipped (RFC 5545: invalid dates are ignored).
            days = start.Day <= length ? [month.AddDays(start.Day - 1)] : [];
        }

        return Filter(days, r with { ByDay = [], ByMonthDay = [] });
    }

    private static IEnumerable<DateTime> Filter(IEnumerable<DateTime> days, RecurrenceRule r) =>
        days.Where(d => (r.ByMonth.Count == 0 || r.ByMonth.Contains(d.Month))
                        && (r.ByMonthDay.Count == 0 || r.ByMonthDay.Any(n => n > 0 ? d.Day == n : d.Day == DateTime.DaysInMonth(d.Year, d.Month) + n + 1))
                        && (r.ByDay.Count == 0 || r.ByDay.Any(b => b.Day == d.DayOfWeek)));
}
