using BlueMail.Application.Features.Calendar;

namespace BlueMail.UnitTests.Calendar;

/// <summary>RRULE expansion, with examples from RFC 5545 3.8.5.3.</summary>
public sealed class RecurrenceTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Hanoi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    private static DateTimeOffset At(TimeZoneInfo z, int y, int m, int d, int h = 9, int min = 0)
    {
        var local = new DateTime(y, m, d, h, min, 0);
        return new DateTimeOffset(local, z.GetUtcOffset(local));
    }

    private static List<DateTime> Local(IEnumerable<DateTimeOffset> xs, TimeZoneInfo z) => xs.Select(x => TimeZoneInfo.ConvertTime(x, z).DateTime).ToList();

    private static IReadOnlyList<DateTimeOffset> Run(string rule, DateTimeOffset start, TimeZoneInfo z, DateTimeOffset from, DateTimeOffset to) =>
        Recurrence.Expand(start, TimeSpan.FromHours(1), RecurrenceRule.Parse(rule), z, from, to);

    [Fact]
    public void Daily_for_10_occurrences()
    {
        var start = At(NewYork, 1997, 9, 2);
        var r = Run("FREQ=DAILY;COUNT=10", start, NewYork, start, start.AddYears(1));
        Local(r, NewYork).Should().Equal(Enumerable.Range(0, 10).Select(i => new DateTime(1997, 9, 2, 9, 0, 0).AddDays(i)));
    }

    [Fact]
    public void Weekly_keeps_the_wall_time_across_daylight_saving()
    {
        // 1997-10-26 is the end of daylight saving in New York: 09:00 local stays 09:00, the UTC offset changes.
        var start = At(NewYork, 1997, 10, 21);
        var r = Run("FREQ=WEEKLY;COUNT=3", start, NewYork, start, start.AddMonths(2));
        Local(r, NewYork).Select(d => d.Hour).Should().AllBeEquivalentTo(9);
        r[0].Offset.Should().Be(TimeSpan.FromHours(-4));
        r[1].Offset.Should().Be(TimeSpan.FromHours(-5));
    }

    [Fact]
    public void Every_other_week_on_monday_wednesday_friday_until_a_date()
    {
        // RFC 5545: "Every other week on Monday, Wednesday, and Friday until December 24, 1997, starting on Monday, September 1, 1997".
        var start = At(NewYork, 1997, 9, 1);
        var r = Run("FREQ=WEEKLY;INTERVAL=2;UNTIL=19971224T000000Z;WKST=SU;BYDAY=MO,WE,FR", start, NewYork, start, start.AddYears(1));
        r.Should().HaveCount(25);
        Local(r, NewYork).Take(6).Select(d => d.Day).Should().Equal(1, 3, 5, 15, 17, 19);
        Local(r, NewYork).Last().Should().Be(new DateTime(1997, 12, 22, 9, 0, 0));
    }

    [Fact]
    public void Monthly_on_the_first_friday_and_the_last_day()
    {
        var start = At(Hanoi, 2026, 1, 2);
        Local(Run("FREQ=MONTHLY;COUNT=4;BYDAY=1FR", start, Hanoi, start, start.AddYears(1)), Hanoi).Select(d => d.ToString("yyyy-MM-dd"))
            .Should().Equal("2026-01-02", "2026-02-06", "2026-03-06", "2026-04-03");
        var end = At(Hanoi, 2026, 1, 31);
        Local(Run("FREQ=MONTHLY;COUNT=3;BYMONTHDAY=-1", end, Hanoi, end, end.AddYears(1)), Hanoi).Select(d => d.Day).Should().Equal(31, 28, 31);
    }

    [Fact]
    public void Monthly_on_the_31st_skips_short_months()
    {
        var start = At(Hanoi, 2026, 1, 31);
        Local(Run("FREQ=MONTHLY;COUNT=4", start, Hanoi, start, start.AddYears(2)), Hanoi).Select(d => d.Month).Should().Equal(1, 3, 5, 7);
    }

    [Fact]
    public void Yearly_birthday_and_the_window_only_returns_what_overlaps()
    {
        var start = At(Hanoi, 2000, 2, 29);
        var r = Run("FREQ=YEARLY", start, Hanoi, At(Hanoi, 2020, 1, 1), At(Hanoi, 2030, 1, 1));
        Local(r, Hanoi).Select(d => d.Year).Should().Equal(2020, 2024, 2028);
    }

    [Fact]
    public void Excluded_dates_and_events_without_a_rule()
    {
        var start = At(Hanoi, 2026, 11, 2);
        var r = Recurrence.Expand(start, TimeSpan.FromHours(1), RecurrenceRule.Parse("FREQ=DAILY;COUNT=3"), Hanoi, start, start.AddDays(10), [start.AddDays(1)]);
        r.Should().Equal(start, start.AddDays(2));
        Recurrence.Expand(start, TimeSpan.FromHours(1), null, Hanoi, start.AddMinutes(30), start.AddDays(1)).Should().Equal(start);
        Recurrence.Expand(start, TimeSpan.FromHours(1), null, Hanoi, start.AddHours(1), start.AddDays(1)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("FREQ=SECONDLY")]
    [InlineData("FREQ=DAILY;BYSETPOS=1")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]
    [InlineData("COUNT=3")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    public void Rules_we_cannot_expand_parse_to_null(string rule) => RecurrenceRule.Parse(rule).Should().BeNull();

    [Fact]
    public void Huge_intervals_and_endless_rules_stop()
    {
        var start = At(Hanoi, 2026, 1, 1);
        Run("FREQ=DAILY;INTERVAL=1000", start, Hanoi, start, start.AddYears(100)).Should().HaveCount(37);
        Run("FREQ=DAILY", start, Hanoi, start, start.AddYears(10)).Should().HaveCount(Recurrence.MaxOccurrences);
        Run("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30", start, Hanoi, start, start.AddYears(5)).Should().BeEmpty();
    }
}
