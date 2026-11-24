using BlueMail.Application.Features.Calendar;

namespace BlueMail.UnitTests.Calendar;

/// <summary>iCalendar (RFC 5545) as Outlook and Google send invitations, and the iTIP REPLY (RFC 5546) we write.</summary>
public sealed class ICalendarTests
{
    // Outlook style: Windows time zone id with its VTIMEZONE, folded lines, escaped text, CN in quotes.
    internal const string OutlookRequest =
        "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nPRODID:Microsoft Exchange Server 2010\r\nVERSION:2.0\r\n" +
        "BEGIN:VTIMEZONE\r\nTZID:SE Asia Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:16010101T000000\r\nTZOFFSETFROM:+0700\r\nTZOFFSETTO:+0700\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n" +
        "BEGIN:VEVENT\r\nORGANIZER;CN=\"Nguyễn Văn An\":mailto:an@doitac.test\r\n" +
        "ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=minh@bluemail.test:mailto:minh@bluemail.test\r\n" +
        "ATTENDEE;ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED;CN=\"Lan, Trần\":mailto:lan@bluemail.test\r\n" +
        "DESCRIPTION;LANGUAGE=vi-VN:Nội dung: tiến độ\\, rủi ro\\nPhòng họp 3\r\n" +
        "UID:040000008200E00074C5B7101A82E00800000000\r\n SUFFIX\r\n" +
        "SUMMARY;LANGUAGE=vi-VN:Họp tiến độ dự án\r\n" +
        "DTSTART;TZID=SE Asia Standard Time:20261126T090000\r\nDTEND;TZID=SE Asia Standard Time:20261126T100000\r\n" +
        "SEQUENCE:2\r\nLOCATION;LANGUAGE=vi-VN:Phòng 3\\; tầng 5\r\nSTATUS:CONFIRMED\r\n" +
        "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Outlook_request_reads_people_times_and_text()
    {
        var e = ICalendar.Parse(OutlookRequest)!;
        e.Method.Should().Be("REQUEST");
        e.Uid.Should().Be("040000008200E00074C5B7101A82E00800000000SUFFIX", "folded lines join (RFC 5545 3.1)");
        e.Sequence.Should().Be(2);
        e.Summary.Should().Be("Họp tiến độ dự án");
        e.Location.Should().Be("Phòng 3; tầng 5");
        e.Description.Should().Be("Nội dung: tiến độ, rủi ro\nPhòng họp 3");
        e.Start.Should().Be(new DateTimeOffset(2026, 11, 26, 2, 0, 0, TimeSpan.Zero));
        e.End.Should().Be(new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero));
        e.AllDay.Should().BeFalse();
        e.Organizer.Should().Be(new CalendarPerson("Nguyễn Văn An", "an@doitac.test", null, null));
        e.Attendees.Select(a => (a.Address, a.PartStat, a.Name)).Should().Equal(
            ("minh@bluemail.test", "NEEDS-ACTION", "minh@bluemail.test"), ("lan@bluemail.test", "ACCEPTED", "Lan, Trần"));
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_its_vtimezone_offset()
    {
        var text = OutlookRequest.Replace("SE Asia Standard Time", "Custom Zone 42", StringComparison.Ordinal);
        ICalendar.Parse(text)!.Start.Should().Be(new DateTimeOffset(2026, 11, 26, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Google_style_utc_all_day_and_duration()
    {
        const string utc = "BEGIN:VCALENDAR\nMETHOD:REQUEST\nBEGIN:VEVENT\nUID:abc@google.com\nDTSTART:20261201T013000Z\nDURATION:PT1H30M\n" +
                           "ORGANIZER:mailto:x@gmail.com\nEND:VEVENT\nEND:VCALENDAR\n";
        var e = ICalendar.Parse(utc)!;
        e.Start.Should().Be(new DateTimeOffset(2026, 12, 1, 1, 30, 0, TimeSpan.Zero));
        e.End.Should().Be(new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero));

        const string day = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:d1\r\nDTSTART;VALUE=DATE:20261224\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var d = ICalendar.Parse(day)!;
        d.Method.Should().Be("PUBLISH");
        d.AllDay.Should().BeTrue();
        d.End.Should().Be(d.Start!.Value.AddDays(1));
    }

    [Fact]
    public void The_master_event_wins_over_an_override()
    {
        const string text = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n" +
                            "BEGIN:VEVENT\r\nUID:r1\r\nRECURRENCE-ID:20261203T090000Z\r\nSUMMARY:Moved one\r\nEND:VEVENT\r\n" +
                            "BEGIN:VEVENT\r\nUID:r1\r\nRRULE:FREQ=WEEKLY;BYDAY=WE\r\nSUMMARY:Weekly\r\nDTSTART:20261126T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var e = ICalendar.Parse(text)!;
        e.Summary.Should().Be("Weekly");
        e.RecurrenceRule.Should().Be("FREQ=WEEKLY;BYDAY=WE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a calendar")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:no uid\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nDTSTART:garbage\r\nDURATION:P99999999D\r\nATTENDEE;CN=\"unterminated:mailto:a@b\r\nEND:VEVENT\r\n")]
    public void Broken_input_gives_null_or_a_partial_event_never_an_exception(string text)
    {
        var act = () => ICalendar.Parse(text);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("PT15M", 15)]
    [InlineData("P1DT2H", 26 * 60)]
    [InlineData("P1W", 7 * 24 * 60)]
    [InlineData("-PT5M", -5)]
    public void Durations(string text, int minutes)
    {
        ICalendar.TryDuration(text, out var span).Should().BeTrue();
        span.Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1H")]
    [InlineData("PT1D")]
    [InlineData("1H")]
    public void Bad_durations(string text) => ICalendar.TryDuration(text, out _).Should().BeFalse();

    [Fact]
    public void Recurring_request_carries_its_zone_and_reads_back_the_same()
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var start = new DateTimeOffset(2026, 12, 7, 8, 0, 0, TimeSpan.Zero); // 09:00 in Paris
        var e = new CalendarEvent("REQUEST", "u1@bluemail", 3, "Weekly; sync", "Room 2", null, start, start.AddMinutes(30), false, "Europe/Paris",
            "FREQ=WEEKLY;COUNT=4", null, new CalendarPerson("Lucas", "lucas@acme.test", null, null),
            [new CalendarPerson("Priya", "priya@acme.test", "NEEDS-ACTION", null)], "CONFIRMED") { ExceptionDates = [start.AddDays(7)] };
        var text = ICalendar.Organizer("REQUEST", e, paris, DateTimeOffset.UtcNow);

        text.Should().Contain("BEGIN:VTIMEZONE").And.Contain("TZID:Europe/Paris").And.Contain("BEGIN:DAYLIGHT")
            .And.Contain("DTSTART;TZID=Europe/Paris:20261207T090000").And.Contain("RRULE:FREQ=WEEKLY;COUNT=4");
        var back = ICalendar.Parse(text)!;
        back.Start.Should().Be(start);
        back.Summary.Should().Be("Weekly; sync");
        back.Sequence.Should().Be(3);
        back.ExceptionDates.Should().Equal(start.AddDays(7));
        back.Attendees.Should().ContainSingle().Which.Address.Should().Be("priya@acme.test");

        // A zone without daylight saving needs only STANDARD; a single event goes in UTC.
        var hanoi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        ICalendar.VTimeZone(hanoi, start).Should().Contain("TZOFFSETTO:+0700").And.NotContain("BEGIN:DAYLIGHT");
        ICalendar.Organizer("CANCEL", e with { RecurrenceRule = null }, paris, DateTimeOffset.UtcNow)
            .Should().Contain("DTSTART:20261207T080000Z").And.Contain("STATUS:CANCELLED").And.NotContain("VTIMEZONE");
    }

    [Fact]
    public void Reply_has_what_rfc_5546_requires_and_folds_long_lines()
    {
        var e = ICalendar.Parse(OutlookRequest)!;
        var longName = "Nguyễn Đức Minh " + new string('x', 80);
        var reply = ICalendar.Reply(e, new CalendarPerson(longName, "minh@bluemail.test", null, null), "ACCEPTED",
            new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));

        foreach (var line in reply.Split("\r\n"))
        {
            System.Text.Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        }

        var back = ICalendar.Parse(reply)!;
        back.Method.Should().Be("REPLY");
        back.Uid.Should().Be(e.Uid);
        back.Sequence.Should().Be(2);
        back.Organizer!.Address.Should().Be("an@doitac.test");
        back.Attendees.Should().ContainSingle().Which.Should().Be(new CalendarPerson(longName, "minh@bluemail.test", "ACCEPTED", null));
        back.Start.Should().Be(e.Start);
        back.Summary.Should().Be(e.Summary);
        reply.Should().Contain("DTSTAMP:20261124T100000Z");
    }
}
