using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 4 web API: list, read, act, sync, attachments - over HTTP with a real BlueID token.</summary>
[Collection(StackCollection.Name)]
public class MailReadingApiTests(StackFixture stack)
{
    private async Task<(Guid Tenant, Guid Mailbox)> MailboxOf(string email)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.PrimaryEmail == email);
        return (user.TenantId, await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == user.Id).Select(m => m.Id).SingleAsync());
    }

    private async Task<DeliveredMessage> Deliver(string to, string raw, IReadOnlyDictionary<string, VirusStatus>? status = null)
    {
        var (tenant, mailbox) = await MailboxOf(to);
        await using var scope = stack.ApiScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var s = new MemoryStream(Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n")));
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        s.Position = 0;
        return (await store.DeliverAsync(new DeliveryRequest(tenant, s, facts, [new MailboxDelivery(mailbox, [SystemLabels.Inbox])], DateTimeOffset.UtcNow,
            AttachmentStatus: status)))[0];
    }

    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> LabelId(HttpClient api, string key)
    {
        var labels = await Data(await api.GetAsync("/api/v1/mail/labels"));
        return labels.GetProperty("labels").EnumerateArray().First(l => l.GetProperty("systemKey").GetString() == key).GetProperty("id").GetGuid();
    }

    private static string Path(string signedUrl) => new Uri(signedUrl).PathAndQuery;

    [Fact]
    public async Task Inbox_list_thread_body_and_cursor_paging()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
        {
            await Deliver("thao@bluemail.test", $"From: \"Đối tác {i}\" <p{i}@doitac.test>\nTo: thao@bluemail.test\nSubject: page {tag} {i}\nMessage-ID: <{tag}{i}@t>\n\nbody {i}\n");
        }

        using var api = await Client("thao@bluemail.test");
        var inbox = await LabelId(api, SystemLabels.Inbox);
        var first = await Data(await api.GetAsync($"/api/v1/mail/conversations?labelId={inbox}&limit=2"));
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.GetProperty("total").GetInt64().Should().BeGreaterThanOrEqualTo(3);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNull();
        var second = await Data(await api.GetAsync($"/api/v1/mail/conversations?labelId={inbox}&limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
        var ids1 = first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("threadId").GetGuid());
        var ids2 = second.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("threadId").GetGuid());
        ids1.Intersect(ids2).Should().BeEmpty("cursor pages never overlap");

        var top = first.GetProperty("items")[0];
        top.GetProperty("subject").GetString().Should().Be($"page {tag} 2", "newest first");
        top.GetProperty("participants")[0].GetProperty("name").GetString().Should().Be("Đối tác 2");

        var thread = await Data(await api.GetAsync($"/api/v1/mail/threads/{top.GetProperty("threadId").GetGuid()}"));
        var msg = thread.GetProperty("messages")[0].GetProperty("message");
        var body = await Data(await api.GetAsync($"/api/v1/mail/messages/{msg.GetProperty("id").GetGuid()}/body"));
        body.GetProperty("text").GetString().Should().Contain("body 2");
    }

    [Fact]
    public async Task Html_body_is_sanitized_and_remote_images_follow_the_trust_choice()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var d = await Deliver("kenji@bluemail.test", $"""
            From: news@shop.test
            To: kenji@bluemail.test
            Subject: html {tag}
            Message-ID: <{tag}@shop.test>
            MIME-Version: 1.0
            Content-Type: text/html; charset=utf-8

            <p onclick="steal()">Sale</p><script>alert(1)</script><img src="https://track.shop.test/o.gif"><a href="javascript:x()">x</a>
            """);
        using var api = await Client("kenji@bluemail.test");
        var body = await Data(await api.GetAsync($"/api/v1/mail/messages/{d.MessageId}/body"));
        var html = body.GetProperty("htmlDocument").GetString()!;
        html.Should().Contain("Sale").And.NotContain("<script").And.NotContain("onclick").And.NotContain("javascript:").And.NotContain("track.shop.test");
        html.Should().Contain("Content-Security-Policy");
        body.GetProperty("hasRemoteImages").GetBoolean().Should().BeTrue();
        body.GetProperty("remoteImagesShown").GetBoolean().Should().BeFalse();

        (await api.PostAsync($"/api/v1/mail/messages/{d.MessageId}/trust-images", null)).EnsureSuccessStatusCode();
        var again = await Data(await api.GetAsync($"/api/v1/mail/messages/{d.MessageId}/body"));
        again.GetProperty("senderTrusted").GetBoolean().Should().BeTrue();
        again.GetProperty("htmlDocument").GetString().Should().Contain("/api/v1/mail/img/", "images now go through the proxy");
    }

    [Fact]
    public async Task Actions_update_lists_counts_and_incremental_changes()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var d = await Deliver("anna@bluemail.test", $"From: x@doitac.test\nTo: anna@bluemail.test\nSubject: act {tag}\nMessage-ID: <{tag}@t>\n\nhello\n");
        using var api = await Client("anna@bluemail.test");
        var inbox = await LabelId(api, SystemLabels.Inbox);
        var trash = await LabelId(api, SystemLabels.Trash);
        var starred = await LabelId(api, SystemLabels.Starred);
        var since = (await Data(await api.GetAsync("/api/v1/mail/changes?sinceModseq=0"))).GetProperty("highestModseq").GetInt64();

        async Task Act(string action, Guid? label = null) =>
            (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action, threadIds = new[] { d.ThreadId }, labelId = label })).EnsureSuccessStatusCode();

        await Act("markRead");
        await Act("star");
        var changes = await Data(await api.GetAsync($"/api/v1/mail/changes?sinceModseq={since}"));
        var entry = changes.GetProperty("changed").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == d.MessageId);
        entry.GetProperty("seen").GetBoolean().Should().BeTrue();
        entry.GetProperty("starred").GetBoolean().Should().BeTrue();
        entry.GetProperty("labelIds").EnumerateArray().Select(x => x.GetGuid()).Should().Contain([inbox, starred]);

        // A user label: create, apply, then delete the label (the message stays).
        var labelId = await Data(await api.PostAsJsonAsync("/api/v1/mail/labels", new { name = "Dự án " + tag, colorBg = "#16a765" }));
        await Act("addLabel", labelId.GetGuid());
        var inLabel = await Data(await api.GetAsync($"/api/v1/mail/conversations?labelId={labelId.GetGuid()}"));
        inLabel.GetProperty("items").GetArrayLength().Should().Be(1);
        (await api.PostAsJsonAsync("/api/v1/mail/labels", new { name = "Dự án " + tag })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await Act("trash");
        (await Data(await api.GetAsync($"/api/v1/mail/conversations?labelId={trash}"))).GetProperty("items").EnumerateArray()
            .Should().Contain(x => x.GetProperty("threadId").GetGuid() == d.ThreadId);
        (await Data(await api.GetAsync("/api/v1/mail/conversations?view=all"))).GetProperty("items").EnumerateArray()
            .Should().NotContain(x => x.GetProperty("threadId").GetGuid() == d.ThreadId, "All Mail hides Trash");

        (await api.DeleteAsync($"/api/v1/mail/labels/{labelId.GetGuid()}")).EnsureSuccessStatusCode();
        await Act("deleteForever");
        var after = await Data(await api.GetAsync($"/api/v1/mail/changes?sinceModseq={since}"));
        after.GetProperty("removed").EnumerateArray().Select(x => x.GetGuid()).Should().Contain(d.MessageId);
        (await api.GetAsync($"/api/v1/mail/threads/{d.ThreadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // IMAP history is consistent: every UID ever added to INBOX for this message was later removed.
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var log = await db.LabelChanges.IgnoreQueryFilters().Where(c => c.LabelId == inbox).ToListAsync();
        log.Where(c => c.Change == LabelChangeKind.Removed).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delete_forever_outside_trash_is_refused()
    {
        var d = await Deliver("somchai@bluemail.test", $"From: x@doitac.test\nTo: somchai@bluemail.test\nSubject: keep\nMessage-ID: <{Guid.NewGuid()}@t>\n\nx\n");
        using var api = await Client("somchai@bluemail.test");
        var r = await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "deleteForever", messageIds = new[] { d.MessageId } });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Attachments_download_via_signed_url_infected_ones_never()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var raw = $"""
            From: a@doitac.test
            To: tuan@bluemail.test
            Subject: files {tag}
            Message-ID: <{tag}@t>
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain; charset=utf-8

            xem tệp
            --b
            Content-Type: text/plain; name="ghi-chú.txt"
            Content-Disposition: attachment; filename*=utf-8''ghi-ch%C3%BA.txt

            nội dung tệp
            --b
            Content-Type: application/octet-stream; name="bad.bin"
            Content-Disposition: attachment; filename="bad.bin"

            BADBYTES
            --b--
            """;
        var d = await Deliver("tuan@bluemail.test", raw, new Dictionary<string, VirusStatus> { ["2"] = VirusStatus.Clean, ["3"] = VirusStatus.Infected });
        using var api = await Client("tuan@bluemail.test");
        var thread = await Data(await api.GetAsync($"/api/v1/mail/threads/{d.ThreadId}"));
        var atts = thread.GetProperty("messages")[0].GetProperty("attachments").EnumerateArray().ToList();
        var note = atts.Single(a => a.GetProperty("fileName").GetString() == "ghi-chú.txt");
        var bad = atts.Single(a => a.GetProperty("fileName").GetString() == "bad.bin");
        bad.GetProperty("downloadUrl").ValueKind.Should().Be(JsonValueKind.Null);

        using var anon = stack.Api.CreateClient(); // signed URLs need no bearer token
        var file = await anon.GetAsync(Path(note.GetProperty("downloadUrl").GetString()!));
        file.EnsureSuccessStatusCode();
        (await file.Content.ReadAsStringAsync()).Should().Contain("nội dung tệp");
        file.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        file.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        var rawEml = await anon.GetAsync(Path(thread.GetProperty("messages")[0].GetProperty("rawUrl").GetString()!));
        (await rawEml.Content.ReadAsStringAsync()).Should().Contain($"Subject: files {tag}");

        var token = new Uri(note.GetProperty("downloadUrl").GetString()!).Segments[^1];
        (await anon.GetAsync("/api/v1/mail/p/" + token[..^3] + "AAA")).StatusCode.Should().Be(HttpStatusCode.NotFound, "tampered signature");
        (await anon.GetAsync("/api/v1/mail/img/" + token)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a part token is not an image-proxy token");
    }

    [Fact]
    public async Task Other_users_and_tenants_cannot_see_or_change_a_thread()
    {
        var d = await Deliver("quang@bluemail.test", $"From: x@doitac.test\nTo: quang@bluemail.test\nSubject: private\nMessage-ID: <{Guid.NewGuid()}@t>\n\nsecret\n");
        using var sameTenant = await Client("yen@bluemail.test");
        using var otherTenant = await Client("jiwoo@acme.test");
        foreach (var api in new[] { sameTenant, otherTenant })
        {
            (await api.GetAsync($"/api/v1/mail/threads/{d.ThreadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await api.GetAsync($"/api/v1/mail/messages/{d.MessageId}/body")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var act = await Data(await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "trash", threadIds = new[] { d.ThreadId } }));
            act.GetProperty("messages").GetInt32().Should().Be(0, "ids outside the caller's mailbox are ignored");
        }

        (await sameTenant.GetAsync($"/api/v1/mail/conversations?labelId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hub_pushes_a_change_when_mail_arrives_and_refuses_foreign_mailboxes()
    {
        var token = await OidcLogin.LoginAsync(stack, "hoa@bluemail.test", StackFixture.UserPassword);
        await using var hub = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(stack.Api.Server.BaseAddress, "/hubs/mail"), o =>
            {
                o.HttpMessageHandlerFactory = _ => stack.Api.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; // TestServer has no WebSockets
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var got = new TaskCompletionSource<JsonElement>();
        hub.On<JsonElement>("mailboxChanged", e => got.TrySetResult(e));
        await hub.StartAsync();
        var mailbox = await hub.InvokeAsync<Guid>("Subscribe", (Guid?)null);
        mailbox.Should().Be((await MailboxOf("hoa@bluemail.test")).Mailbox);

        var (_, foreign) = await MailboxOf("lucas@acme.test");
        Func<Task> act = () => hub.InvokeAsync<Guid>("Subscribe", (Guid?)foreign);
        await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();

        await Deliver("hoa@bluemail.test", $"From: x@doitac.test\nTo: hoa@bluemail.test\nSubject: live\nMessage-ID: <{Guid.NewGuid()}@t>\n\nx\n");
        var e = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        e.GetProperty("mailboxId").GetGuid().Should().Be(mailbox);
        e.GetProperty("kind").GetString().Should().Be("message.added");
    }
}
