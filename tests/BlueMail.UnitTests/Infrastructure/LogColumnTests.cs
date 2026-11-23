using BlueMail.Infrastructure;
using Serilog.Events;
using Serilog.Parsing;

namespace BlueMail.UnitTests.Infrastructure;

/// <summary>BUG-021: Npgsql refuses DateTimeOffset with a non-zero offset for timestamptz; logs written on a UTC+7 host were lost.</summary>
public class LogColumnTests
{
    [Fact]
    public void Timestamp_column_is_always_utc()
    {
        var local = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.FromHours(7));
        var e = new LogEvent(local, LogEventLevel.Warning, null, new MessageTemplateParser().Parse("x"), []);
        var value = (DateTimeOffset)new UtcTimestampColumnWriter().GetValue(e)!;
        value.Offset.Should().Be(TimeSpan.Zero);
        value.Should().Be(local);
    }
}
