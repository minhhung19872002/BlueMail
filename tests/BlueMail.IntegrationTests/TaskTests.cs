using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueMail.IntegrationTests.Infrastructure;

namespace BlueMail.IntegrationTests;

/// <summary>To-dos (Outlook To Do): smart lists, completing, making a task from a message, ownership.</summary>
[Collection(StackCollection.Name)]
public class TaskTests(StackFixture stack)
{
    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static async Task<List<string>> Titles(HttpClient api, string view) =>
        (await Data(await api.GetAsync($"/api/v1/tasks?view={view}"))).EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToList();

    [Fact]
    public async Task Smart_lists_completion_from_a_message_and_ownership()
    {
        using var joao = await Client("joao@acme.test");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var plain = await Data(await joao.PostAsJsonAsync("/api/v1/tasks", new { title = "Call the bank " + tag }));
        var due = await Data(await joao.PostAsJsonAsync("/api/v1/tasks", new { title = "Send report " + tag, dueDate = "2026-12-01", important = true }));
        (await joao.PostAsJsonAsync("/api/v1/tasks", new { title = "  " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await joao.GetAsync("/api/v1/tasks?view=nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var my = await Titles(joao, "my");
        my.IndexOf("Send report " + tag).Should().BeLessThan(my.IndexOf("Call the bank " + tag), "important first");
        (await Titles(joao, "important")).Should().Contain("Send report " + tag).And.NotContain("Call the bank " + tag);
        (await Titles(joao, "planned")).Should().Contain("Send report " + tag).And.NotContain("Call the bank " + tag);

        // Completing moves it to "completed"; un-completing brings it back.
        var id = plain.GetProperty("id").GetGuid();
        var done = await Data(await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "Call the bank " + tag, completed = true }));
        done.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
        (await Titles(joao, "my")).Should().NotContain("Call the bank " + tag);
        (await Titles(joao, "completed")).Should().Contain("Call the bank " + tag);
        await Data(await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "Call the bank " + tag, completed = false }));
        (await Titles(joao, "my")).Should().Contain("Call the bank " + tag);

        // From a message: the subject becomes the title and the task links to the conversation.
        await Data(await joao.PostAsJsonAsync("/api/v1/mail/drafts", new
        {
            state = new { to = new[] { new { address = "amara@acme.test" } }, subject = "Budget " + tag, html = "<p>x</p>", attachments = Array.Empty<object>() },
        }));
        var inbox = await Data(await joao.GetAsync("/api/v1/mail/conversations?view=all&limit=100"));
        var first = inbox.GetProperty("items").EnumerateArray().First(c => c.GetProperty("subject").GetString() == "Budget " + tag);
        var fromMail = await Data(await joao.PostAsJsonAsync("/api/v1/tasks", new { messageId = first.GetProperty("latestMessageId").GetGuid() }));
        fromMail.GetProperty("title").GetString().Should().Be(first.GetProperty("subject").GetString() is { Length: > 0 } s ? s : "(?)");
        fromMail.GetProperty("threadId").GetGuid().Should().Be(first.GetProperty("threadId").GetGuid());

        // Another user can neither see, change, delete, nor make tasks from joao's mail.
        using var amara = await Client("amara@acme.test");
        (await Titles(amara, "my")).Should().NotContain("Send report " + tag);
        (await amara.PutAsJsonAsync($"/api/v1/tasks/{due.GetProperty("id").GetGuid()}", new { title = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await amara.DeleteAsync($"/api/v1/tasks/{due.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await amara.PostAsJsonAsync("/api/v1/tasks", new { messageId = first.GetProperty("latestMessageId").GetGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Categorize (a colour of the calendar palette).
        var coloured = await Data(await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "Call the bank " + tag, category = "green" }));
        coloured.GetProperty("category").GetString().Should().Be("green");
        // Reminder time (kept as UTC); clearing it.
        var reminded = await Data(await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "Call the bank " + tag, category = "green", reminderAt = "2026-12-01T09:30:00+07:00" }));
        DateTimeOffset.Parse(reminded.GetProperty("reminderAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(new DateTimeOffset(2026, 12, 1, 2, 30, 0, TimeSpan.Zero));
        (await Data(await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "Call the bank " + tag, category = "green" })))
            .GetProperty("reminderAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await joao.PutAsJsonAsync($"/api/v1/tasks/{id}", new { title = "x", category = "pink" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Assign (same organisation): amara gets a copy; completing it completes joao's task.
        var dueId = due.GetProperty("id").GetGuid();
        (await joao.PostAsJsonAsync($"/api/v1/tasks/{dueId}/assign", new { email = "joao@acme.test" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await joao.PostAsJsonAsync($"/api/v1/tasks/{dueId}/assign", new { email = "nobody@acme.test" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var assigned = await Data(await joao.PostAsJsonAsync($"/api/v1/tasks/{dueId}/assign", new { email = "amara@acme.test" }));
        assigned.GetProperty("assignedTo").GetString().Should().NotBeNullOrEmpty();
        (await joao.PostAsJsonAsync($"/api/v1/tasks/{dueId}/assign", new { email = "amara@acme.test" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "already assigned");
        var theirs = (await Data(await amara.GetAsync("/api/v1/tasks?view=my"))).EnumerateArray().Single(x => x.GetProperty("title").GetString() == "Send report " + tag);
        theirs.GetProperty("assignedBy").GetString().Should().NotBeNullOrEmpty();
        theirs.GetProperty("dueDate").GetString().Should().Be("2026-12-01");
        await Data(await amara.PutAsJsonAsync($"/api/v1/tasks/{theirs.GetProperty("id").GetGuid()}", new { title = "Send report " + tag, dueDate = "2026-12-01", completed = true }));
        (await Titles(joao, "completed")).Should().Contain("Send report " + tag, "the assigner's task completes with the assignee's");
        await Data(await amara.DeleteAsync($"/api/v1/tasks/{theirs.GetProperty("id").GetGuid()}"));

        await Data(await joao.DeleteAsync($"/api/v1/tasks/{due.GetProperty("id").GetGuid()}"));
        (await Titles(joao, "completed")).Should().NotContain("Send report " + tag);
    }
}
