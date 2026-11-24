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
}
