using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueMail.IntegrationTests.Infrastructure;

namespace BlueMail.IntegrationTests;

/// <summary>Calendar: events, recurrence, invitations by iMIP between two users, answers and cancellations.</summary>
[Collection(StackCollection.Name)]
public class CalendarTests(StackFixture stack)
{
    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static string Range(DateTimeOffset from, DateTimeOffset to) =>
        $"/api/v1/calendar/events?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static async Task<Guid> LatestMessage(HttpClient api, string subject)
    {
        var list = await Data(await api.GetAsync("/api/v1/mail/conversations?view=all&limit=100"));
        return list.GetProperty("items").EnumerateArray().First(c => c.GetProperty("subject").GetString() == subject).GetProperty("latestMessageId").GetGuid();
    }

    [Fact]
    public async Task Weekly_meeting_invite_answer_skip_one_and_cancel()
    {
        using var lucas = await Client("lucas@acme.test");
        using var priya = await Client("priya@acme.test");
        var title = "Weekly sync " + Guid.NewGuid().ToString("N")[..6];
        var start = new DateTimeOffset(2026, 12, 7, 9, 0, 0, TimeSpan.FromHours(1)); // a Monday, Paris
        var created = await Data(await lucas.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            summary = title, location = "Room 2", start, end = start.AddMinutes(30), allDay = false, timeZone = "Europe/Paris",
            recurrenceRule = "FREQ=WEEKLY;COUNT=4", attendees = new[] { new { name = "Priya", address = "priya@acme.test" } }, reminderMinutes = 10,
        }));
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("isOrganizer").GetBoolean().Should().BeTrue();

        var window = await Data(await lucas.GetAsync(Range(start.AddDays(-1), start.AddDays(60))));
        var mine = window.EnumerateArray().Where(e => e.GetProperty("id").GetGuid() == id).ToList();
        mine.Should().HaveCount(4);
        mine.Select(e => e.GetProperty("start").GetDateTimeOffset().ToOffset(TimeSpan.FromHours(1)).Hour).Should().AllBeEquivalentTo(9);

        // Priya gets the invitation and accepts: the event appears in her calendar.
        var invite = await LatestMessage(priya, "Invitation: " + title);
        var card = await Data(await priya.GetAsync($"/api/v1/mail/messages/{invite}/invite"));
        card.GetProperty("recurring").GetBoolean().Should().BeTrue();
        card.GetProperty("canRespond").GetBoolean().Should().BeTrue();
        await Data(await priya.PostAsJsonAsync($"/api/v1/mail/messages/{invite}/invite/respond", new { response = "ACCEPTED" }));
        var hers = (await Data(await priya.GetAsync(Range(start.AddDays(-1), start.AddDays(60))))).EnumerateArray()
            .Where(e => e.GetProperty("summary").GetString() == title).ToList();
        hers.Should().HaveCount(4);
        hers[0].GetProperty("myResponse").GetString().Should().Be("ACCEPTED");
        hers[0].GetProperty("isOrganizer").GetBoolean().Should().BeFalse();

        // Lucas opens her answer: his event shows Priya as accepted.
        var reply = await LatestMessage(lucas, "Accepted: " + title);
        await Data(await lucas.GetAsync($"/api/v1/mail/messages/{reply}/invite"));
        var his = (await Data(await lucas.GetAsync(Range(start, start.AddDays(1))))).EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        his.GetProperty("attendees")[0].GetProperty("response").GetString().Should().Be("ACCEPTED");

        // Skip the second week: three occurrences left.
        var second = start.AddDays(7);
        await Data(await lucas.DeleteAsync($"/api/v1/calendar/events/{id}?occurrence={Uri.EscapeDataString(second.ToString("O"))}"));
        (await Data(await lucas.GetAsync(Range(start.AddDays(-1), start.AddDays(60))))).EnumerateArray()
            .Count(e => e.GetProperty("id").GetGuid() == id).Should().Be(3);

        // Cancel the whole series: Priya receives the cancellation; opening it removes the meeting from her calendar.
        await Data(await lucas.DeleteAsync($"/api/v1/calendar/events/{id}"));
        var cancel = await LatestMessage(priya, "Cancelled: " + title);
        (await Data(await priya.GetAsync($"/api/v1/mail/messages/{cancel}/invite"))).GetProperty("cancelled").GetBoolean().Should().BeTrue();
        (await Data(await priya.GetAsync(Range(start.AddDays(-1), start.AddDays(60))))).EnumerateArray()
            .Should().NotContain(e => e.GetProperty("summary").GetString() == title);
    }

    [Fact]
    public async Task Validation_ownership_and_all_day_events()
    {
        using var sofia = await Client("sofia@acme.test");
        var day = new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero);
        (await sofia.PostAsJsonAsync("/api/v1/calendar/events", new { summary = "x", start = day, end = day.AddHours(-1), allDay = false }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sofia.PostAsJsonAsync("/api/v1/calendar/events", new { summary = "x", start = day, end = day.AddHours(1), allDay = false, recurrenceRule = "FREQ=SECONDLY" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sofia.GetAsync(Range(day, day.AddDays(500)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var holiday = await Data(await sofia.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            summary = "Christmas Eve", start = day, end = day.AddDays(1), allDay = true, timeZone = "Europe/Madrid",
        }));
        holiday.GetProperty("allDay").GetBoolean().Should().BeTrue();
        holiday.GetProperty("start").GetDateTimeOffset().Should().Be(day);

        // Someone else's event can be neither seen nor changed.
        using var jonas = await Client("jonas@acme.test");
        (await Data(await jonas.GetAsync(Range(day.AddDays(-1), day.AddDays(2))))).EnumerateArray()
            .Should().NotContain(e => e.GetProperty("summary").GetString() == "Christmas Eve");
        (await jonas.DeleteAsync($"/api/v1/calendar/events/{holiday.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Free_busy_shows_times_without_details_and_only_inside_the_organisation()
    {
        using var joao = await Client("joao@acme.test");
        var day = new DateTimeOffset(2027, 1, 12, 0, 0, 0, TimeSpan.Zero);
        await Data(await joao.PostAsJsonAsync("/api/v1/calendar/events", new { summary = "Secret 1:1", start = day.AddHours(9), end = day.AddHours(10), allDay = false, timeZone = "UTC" }));
        await Data(await joao.PostAsJsonAsync("/api/v1/calendar/events", new { summary = "Overlap", start = day.AddHours(9.5), end = day.AddHours(11), allDay = false, timeZone = "UTC" }));
        await Data(await joao.PostAsJsonAsync("/api/v1/calendar/events", new { summary = "Daily", start = day.AddHours(14), end = day.AddHours(14.25), allDay = false, timeZone = "UTC",
            recurrenceRule = "FREQ=DAILY;COUNT=3" }));

        using var lucas = await Client("lucas@acme.test");
        var url = $"/api/v1/calendar/freebusy?address=JOAO@acme.test&address=someone@doitac.test&address=minh@bluemail.test&from={Uri.EscapeDataString(day.ToString("O"))}&to={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}";
        var body = await (await lucas.GetAsync(url)).Content.ReadAsStringAsync();
        body.Should().NotContain("Secret").And.NotContain("Overlap");
        var list = JsonDocument.Parse(body).RootElement.GetProperty("data").EnumerateArray().ToList();
        var joaoBusy = list.Single(x => x.GetProperty("address").GetString() == "joao@acme.test");
        joaoBusy.GetProperty("known").GetBoolean().Should().BeTrue();
        joaoBusy.GetProperty("busy").EnumerateArray().Select(b => (b.GetProperty("start").GetDateTimeOffset(), b.GetProperty("end").GetDateTimeOffset()))
            .Should().Equal((day.AddHours(9), day.AddHours(11)), (day.AddHours(14), day.AddHours(14.25)));
        list.Single(x => x.GetProperty("address").GetString() == "someone@doitac.test").GetProperty("known").GetBoolean().Should().BeFalse();
        list.Single(x => x.GetProperty("address").GetString() == "minh@bluemail.test").GetProperty("known").GetBoolean().Should().BeFalse("another organisation");

        (await lucas.GetAsync($"/api/v1/calendar/freebusy?address=joao@acme.test&from={Uri.EscapeDataString(day.ToString("O"))}&to={Uri.EscapeDataString(day.AddDays(40).ToString("O"))}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sharing_a_calendar_to_view_or_edit_and_stopping()
    {
        using var sofia = await Client("sofia@acme.test");
        using var tomas = await Client("tomas@acme.test");
        var tomasId = (await Data(await tomas.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid();
        var sofiaId = (await Data(await sofia.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid();
        var day = new DateTimeOffset(2027, 2, 3, 0, 0, 0, TimeSpan.Zero);
        var title = "Dentist " + Guid.NewGuid().ToString("N")[..5];
        await Data(await sofia.PostAsJsonAsync("/api/v1/calendar/events", new { summary = title, start = day.AddHours(15), end = day.AddHours(16), allDay = false, timeZone = "UTC" }));
        var range = Range(day, day.AddDays(1));

        // Not shared: tomas cannot open it (looks like "not found").
        (await tomas.GetAsync(range + $"&owner={sofiaId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // VIEW: tomas sees the details but cannot add.
        var shares = await Data(await sofia.PutAsJsonAsync("/api/v1/calendar/shares", new { email = "TOMAS@acme.test", permission = "VIEW" }));
        shares.GetProperty("mine").EnumerateArray().Should().ContainSingle(x => x.GetProperty("userId").GetGuid() == tomasId && x.GetProperty("permission").GetString() == "VIEW");
        (await Data(await tomas.GetAsync("/api/v1/calendar/shares"))).GetProperty("withMe").EnumerateArray().Should().Contain(x => x.GetProperty("userId").GetGuid() == sofiaId);
        (await Data(await tomas.GetAsync(range + $"&owner={sofiaId}"))).EnumerateArray().Should().Contain(e => e.GetProperty("summary").GetString() == title);
        (await tomas.PostAsJsonAsync($"/api/v1/calendar/events?owner={sofiaId}", new { summary = "x", start = day.AddHours(9), end = day.AddHours(10), allDay = false }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // EDIT: tomas adds to sofia's calendar, but not a meeting with guests.
        await Data(await sofia.PutAsJsonAsync("/api/v1/calendar/shares", new { email = "tomas@acme.test", permission = "EDIT" }));
        var added = await Data(await tomas.PostAsJsonAsync($"/api/v1/calendar/events?owner={sofiaId}", new { summary = "Booked by Tomas", start = day.AddHours(9), end = day.AddHours(10), allDay = false, timeZone = "UTC" }));
        (await Data(await sofia.GetAsync(range))).EnumerateArray().Should().Contain(e => e.GetProperty("summary").GetString() == "Booked by Tomas");
        (await tomas.PostAsJsonAsync($"/api/v1/calendar/events?owner={sofiaId}", new { summary = "m", start = day.AddHours(11), end = day.AddHours(12), allDay = false,
            attendees = new[] { new { address = "jonas@acme.test" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Data(await tomas.DeleteAsync($"/api/v1/calendar/events/{added.GetProperty("id").GetGuid()}?owner={sofiaId}"))).GetBoolean().Should().BeTrue();

        // Stop sharing: closed again; nobody outside the organisation can be given access.
        await Data(await sofia.PutAsJsonAsync("/api/v1/calendar/shares", new { email = "tomas@acme.test", permission = "NONE" }));
        (await tomas.GetAsync(range + $"&owner={sofiaId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sofia.PutAsJsonAsync("/api/v1/calendar/shares", new { email = "minh@bluemail.test", permission = "VIEW" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sofia.PutAsJsonAsync("/api/v1/calendar/shares", new { email = "sofia@acme.test", permission = "VIEW" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Published_calendar_link_serves_ics_and_can_be_replaced_or_removed()
    {
        using var priya = await Client("priya@acme.test");
        var day = DateTimeOffset.UtcNow.Date.AddDays(3);
        var title = "Board prep " + Guid.NewGuid().ToString("N")[..5];
        await Data(await priya.PostAsJsonAsync("/api/v1/calendar/events", new { summary = title, location = "Room 9", start = new DateTimeOffset(day.AddHours(9), TimeSpan.Zero),
            end = new DateTimeOffset(day.AddHours(10), TimeSpan.Zero), allDay = false, timeZone = "Asia/Kolkata", recurrenceRule = "FREQ=WEEKLY;COUNT=3" }));

        var busy = await Data(await priya.PutAsJsonAsync("/api/v1/calendar/feed", new { detail = "BUSY" }));
        var busyUrl = new Uri(busy.GetProperty("url").GetString()!);
        (await Data(await priya.GetAsync("/api/v1/calendar/feed"))).GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null, "the link is shown once");

        using var anon = stack.Api.CreateClient();
        var res = await anon.GetAsync(busyUrl.PathAndQuery);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        var ics = await res.Content.ReadAsStringAsync();
        ics.Should().Contain("METHOD:PUBLISH").And.Contain("SUMMARY:Busy").And.Contain("RRULE:FREQ=WEEKLY;COUNT=3").And.Contain("TZID:Asia/Kolkata")
            .And.NotContain(title).And.NotContain("Room 9").And.NotContain("ATTENDEE");

        // A FULL link replaces the old one; the old one stops working.
        var full = await Data(await priya.PutAsJsonAsync("/api/v1/calendar/feed", new { detail = "FULL" }));
        (await anon.GetAsync(busyUrl.PathAndQuery)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var fullIcs = await anon.GetStringAsync(new Uri(full.GetProperty("url").GetString()!).PathAndQuery);
        fullIcs.Should().Contain("SUMMARY:" + title).And.Contain("LOCATION:Room 9");
        BlueMail.Application.Features.Calendar.ICalendar.Parse(fullIcs)!.Summary.Should().Be(title);

        await Data(await priya.PutAsJsonAsync("/api/v1/calendar/feed", new { detail = (string?)null }));
        (await anon.GetAsync(new Uri(full.GetProperty("url").GetString()!).PathAndQuery)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync("/api/v1/calendar/feed/not-a-real-token-at-all-xxxxxxxx.ics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

