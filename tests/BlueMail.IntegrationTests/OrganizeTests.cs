using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 8: tabs, importance, mute, snooze, unsubscribe, contacts, settings, Trash clean-up.</summary>
[Collection(StackCollection.Name)]
public class OrganizeTests(StackFixture stack)
{
    private async Task<(Guid Tenant, Guid Mailbox, Guid User)> MailboxOf(string email)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.PrimaryEmail == email);
        return (user.TenantId, await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == user.Id).Select(m => m.Id).SingleAsync(), user.Id);
    }

    private async Task<(Guid Message, Guid Thread)> Receive(string to, string raw)
    {
        var (tenant, mailbox, _) = await MailboxOf(to);
        await using var scope = stack.ApiScope();
        var bytes = Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n"));
        var s = new MemoryStream(bytes);
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        var rules = scope.ServiceProvider.GetRequiredService<MailboxRules>();
        var incoming = new IncomingMessage(facts, bytes, facts.From?.Address, DateTimeOffset.UtcNow, false, false);
        var plan = await rules.PlanAsync(tenant, mailbox, [to], incoming, default);
        s.Position = 0;
        var d = (await scope.ServiceProvider.GetRequiredService<PgMailStore>().DeliverAsync(new DeliveryRequest(tenant, s, facts, [plan.Delivery], incoming.ReceivedAt)))[0];
        await rules.AfterDeliveryAsync(tenant, mailbox, plan, incoming, default, d.ThreadId);
        return (d.MessageId, d.ThreadId);
    }

    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<T> Db<T>(Func<BlueMailDbContext, Task<T>> f)
    {
        await using var scope = stack.ApiScope();
        return await f(scope.ServiceProvider.GetRequiredService<BlueMailDbContext>());
    }

    private Task<List<string?>> Labels(Guid messageId) => Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters()
                                                                    join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                                                    where ml.MessageId == messageId select l.SystemKey ?? l.Name).ToListAsync());

    [Fact]
    public async Task Tabs_and_importance_follow_explainable_rules_and_contacts_win()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        const string to = "anna@bluemail.test";
        (await Labels((await Receive(to, $"From: noti@facebookmail.com\nTo: {to}\nSubject: s {tag}\nMessage-ID: <s{tag}@t>\n\nx\n")).Message))
            .Should().Contain(SystemLabels.CategorySocial);
        (await Labels((await Receive(to, $"From: deals@shop.test\nTo: {to}\nList-Unsubscribe: <https://shop.test/u>\nSubject: p {tag}\nMessage-ID: <p{tag}@t>\n\nx\n")).Message))
            .Should().Contain(SystemLabels.CategoryPromotions);
        (await Labels((await Receive(to, $"From: dev@lists.test\nTo: {to}\nList-Id: <dev.lists.test>\nSubject: f {tag}\nMessage-ID: <f{tag}@t>\n\nx\n")).Message))
            .Should().Contain(SystemLabels.CategoryForums);
        (await Labels((await Receive(to, $"From: no-reply@bank.test\nTo: {to}\nSubject: u {tag}\nMessage-ID: <u{tag}@t>\n\nx\n")).Message))
            .Should().Contain(SystemLabels.CategoryUpdates);

        // A colleague writing directly to me: Primary and important.
        var colleague = await Labels((await Receive(to, $"From: tuan@bluemail.test\nTo: {to}\nSubject: c {tag}\nMessage-ID: <c{tag}@t>\n\nx\n")).Message);
        colleague.Should().Contain([SystemLabels.CategoryPrimary, SystemLabels.Important]);

        // Someone I write to often is in my contacts: always Primary, important.
        using var api = await Client(to);
        var id = await Data(await api.PostAsJsonAsync("/api/v1/contacts", new { displayName = "Shop friend", emails = new[] { new { value = $"friend.{tag}@shop.test" } }, phones = Array.Empty<object>() }));
        await Db(db => db.Contacts.IgnoreQueryFilters().Where(c => c.Id == id.GetGuid()).ExecuteUpdateAsync(c => c.SetProperty(x => x.ContactCount, 5)));
        var friend = await Labels((await Receive(to, $"From: friend.{tag}@shop.test\nTo: {to}\nList-Unsubscribe: <https://shop.test/u>\nSubject: hi {tag}\nMessage-ID: <h{tag}@t>\n\nx\n")).Message);
        friend.Should().Contain(SystemLabels.CategoryPrimary).And.NotContain(SystemLabels.Important, "bulk headers never make mail important");
        var friend2 = await Labels((await Receive(to, $"From: friend.{tag}@shop.test\nTo: {to}\nSubject: hi2 {tag}\nMessage-ID: <h2{tag}@t>\n\nx\n")).Message);
        friend2.Should().Contain([SystemLabels.CategoryPrimary, SystemLabels.Important]);
    }

    [Fact]
    public async Task Muted_conversation_stays_archived_unless_addressed_to_me_and_snooze_comes_back()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        const string me = "kenji@bluemail.test";
        using var api = await Client(me);
        var first = await Receive(me, $"From: a@partner.test\nTo: team@partner.test, {me}\nSubject: thread {tag}\nMessage-ID: <m1{tag}@t>\n\nx\n");
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "mute", threadIds = new[] { first.Thread } })).EnsureSuccessStatusCode();
        (await Labels(first.Message)).Should().NotContain(SystemLabels.Inbox);
        var cc = await Receive(me, $"From: b@partner.test\nTo: team@partner.test\nCc: x@partner.test\nSubject: Re: thread {tag}\nMessage-ID: <m2{tag}@t>\nIn-Reply-To: <m1{tag}@t>\n\nx\n");
        (await Labels(cc.Message)).Should().NotContain(SystemLabels.Inbox, "muted, not addressed to me");
        var direct = await Receive(me, $"From: b@partner.test\nTo: {me}\nSubject: Re: thread {tag}\nMessage-ID: <m3{tag}@t>\nIn-Reply-To: <m1{tag}@t>\n\nx\n");
        (await Labels(direct.Message)).Should().Contain(SystemLabels.Inbox, "addressed to me personally");

        // Snooze: leaves the Inbox; due time brings it back, newest unread.
        var s = await Receive(me, $"From: c@partner.test\nTo: {me}\nSubject: later {tag}\nMessage-ID: <s1{tag}@t>\n\nx\n");
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "markRead", threadIds = new[] { s.Thread } })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "snooze", threadIds = new[] { s.Thread }, snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the time must be in the future");
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "snooze", threadIds = new[] { s.Thread }, snoozeUntil = DateTimeOffset.UtcNow.AddHours(3) })).EnsureSuccessStatusCode();
        (await Labels(s.Message)).Should().Contain(SystemLabels.Snoozed).And.NotContain(SystemLabels.Inbox);
        (await Data(await api.GetAsync("/api/v1/mail/snoozed"))).EnumerateArray().Should().Contain(x => x.GetProperty("threadId").GetGuid() == s.Thread);
        await Db(db => db.Snoozes.IgnoreQueryFilters().Where(x => x.ThreadId == s.Thread).ExecuteUpdateAsync(x => x.SetProperty(y => y.UntilAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        (await stack.Api.Services.GetRequiredService<MailMaintenanceWorker>().WakeDueAsync(default)).Should().BeGreaterThan(0);
        (await Labels(s.Message)).Should().Contain(SystemLabels.Inbox).And.NotContain(SystemLabels.Snoozed);
        (await Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == s.Message))).Seen.Should().BeFalse("it comes back unread");

        // New mail in a snoozed conversation brings it back at once.
        var t = await Receive(me, $"From: d@partner.test\nTo: {me}\nSubject: soon {tag}\nMessage-ID: <t1{tag}@t>\n\nx\n");
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "snooze", threadIds = new[] { t.Thread }, snoozeUntil = DateTimeOffset.UtcNow.AddDays(2) })).EnsureSuccessStatusCode();
        await Receive(me, $"From: d@partner.test\nTo: {me}\nSubject: Re: soon {tag}\nMessage-ID: <t2{tag}@t>\nIn-Reply-To: <t1{tag}@t>\n\nx\n");
        (await Labels(t.Message)).Should().Contain(SystemLabels.Inbox).And.NotContain(SystemLabels.Snoozed);
        (await Db(db => db.Snoozes.IgnoreQueryFilters().AnyAsync(x => x.ThreadId == t.Thread && x.Status == SnoozeStatus.Active))).Should().BeFalse();
    }

    [Fact]
    public async Task Unsubscribe_by_mailto_sends_mail_and_explains_when_impossible()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        const string me = "somchai@bluemail.test";
        using var api = await Client(me);
        var m = await Receive(me, $"From: news@shop.test\nTo: {me}\nList-Unsubscribe: <mailto:leave.{tag}@shop.test?subject=stop>, <https://shop.test/u>\nSubject: n {tag}\nMessage-ID: <n{tag}@t>\n\nx\n");
        var r = await Data(await api.PostAsync($"/api/v1/mail/messages/{m.Message}/unsubscribe", null));
        r.GetProperty("outcome").GetString().Should().Be("DONE");
        (await Db(db => db.OutboundRecipients.IgnoreQueryFilters().AnyAsync(x => x.Rcpt == $"leave.{tag}@shop.test"))).Should().BeTrue();
        var none = await Receive(me, $"From: a@shop.test\nTo: {me}\nSubject: plain {tag}\nMessage-ID: <x{tag}@t>\n\nx\n");
        (await api.PostAsync($"/api/v1/mail/messages/{none.Message}/unsubscribe", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Contacts_import_export_merge_directory_and_autosave()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var api = await Client("thao@bluemail.test");
        var vcf = $"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Nguyễn An {tag}\r\nEMAIL;TYPE=work:an.{tag}@acme.test\r\nTEL;TYPE=cell:0912 345 678\r\nEND:VCARD\r\n" +
                  $"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:An Nguyễn {tag}\r\nEMAIL:AN.{tag}@acme.test\r\nEMAIL:an.{tag}@gmail.test\r\nORG:ACME\r\nEND:VCARD\r\n";
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(vcf)), "file", "c.vcf" } })
        {
            var r = await Data(await api.PostAsync("/api/v1/contacts/import", form));
            (r.GetProperty("created").GetInt32(), r.GetProperty("merged").GetInt32()).Should().Be((1, 1), "the second card shares an address");
        }

        var list = await Data(await api.GetAsync($"/api/v1/contacts?q={tag}"));
        var c = list.GetProperty("items").EnumerateArray().Single();
        c.GetProperty("emails").GetArrayLength().Should().Be(2);
        c.GetProperty("org").GetString().Should().Be("ACME");

        // Duplicates created by hand are merged on request.
        await Data(await api.PostAsJsonAsync("/api/v1/contacts", new { displayName = "Dup", emails = new[] { new { value = $"an.{tag}@gmail.test" } }, phones = Array.Empty<object>() }));
        (await Data(await api.PostAsync("/api/v1/contacts/merge-duplicates", null))).GetInt32().Should().BeGreaterThan(0);
        (await Data(await api.GetAsync($"/api/v1/contacts?q={tag}"))).GetProperty("total").GetInt32().Should().Be(1);

        var vcard = await (await api.GetAsync("/api/v1/contacts/export?format=vcf")).Content.ReadAsStringAsync();
        vcard.Should().Contain($"FN:Nguyễn An {tag}");
        var csv = await (await api.GetAsync("/api/v1/contacts/export?format=csv")).Content.ReadAsStringAsync();
        csv.Should().Contain($"an.{tag}@acme.test");

        // Photo: only real images.
        using (var bad = new MultipartFormDataContent { { new ByteArrayContent("<svg/>"u8.ToArray()), "file", "x.png" } })
        {
            (await api.PutAsync($"/api/v1/contacts/{c.GetProperty("id").GetGuid()}/photo", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Organisation directory: administrators can hide someone.
        var dir = await Data(await api.GetAsync("/api/v1/contacts/directory?q=quang"));
        dir.EnumerateArray().Should().Contain(e => e.GetProperty("email").GetString() == "quang@bluemail.test");
        using var admin = await Client("lan@bluemail.test");
        var quang = dir.EnumerateArray().First(e => e.GetProperty("email").GetString() == "quang@bluemail.test").GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/admin/directory/{quang}", new { hidden = true })).EnsureSuccessStatusCode();
        (await Data(await api.GetAsync("/api/v1/contacts/directory?q=quang"))).EnumerateArray().Should().BeEmpty();
        (await admin.PutAsJsonAsync($"/api/v1/admin/directory/{quang}", new { hidden = false })).EnsureSuccessStatusCode();

        // Suggestions while composing include contacts.
        var sug = await Data(await api.GetAsync($"/api/v1/mail/compose/suggest?q=an.{tag}"));
        sug.EnumerateArray().Should().Contain(s => s.GetProperty("kind").GetString() == "contact");
    }

    [Fact]
    public async Task People_I_write_to_become_other_contacts_counted_once_per_send()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var api = await Client("hoa@bluemail.test");
        var target = $"partner.{tag}@partner.test";
        for (var i = 0; i < 2; i++)
        {
            var draft = await Data(await api.PostAsJsonAsync("/api/v1/mail/drafts", new { state = new { to = new[] { new { name = "Đối tác", address = target } }, subject = "s " + tag, html = "<p>x</p>" } }));
            var send = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draft.GetProperty("id").GetGuid()}/send", new { undoSeconds = 5 }));
            var outgoing = send.GetProperty("outgoingId").GetGuid();
            await Db(db => db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == outgoing).ExecuteUpdateAsync(o => o.SetProperty(x => x.SendAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
            await stack.Api.Services.GetRequiredService<BlueMail.Application.Features.Sending.OutgoingDispatcher>().RunOnceAsync(default);
        }

        var other = await Data(await api.GetAsync($"/api/v1/contacts?other=true&q={tag}"));
        var c = other.GetProperty("items").EnumerateArray().Single();
        c.GetProperty("displayName").GetString().Should().Be("Đối tác");
        c.GetProperty("isAutoSaved").GetBoolean().Should().BeTrue();
        (await Db(db => db.Contacts.IgnoreQueryFilters().SingleAsync(x => x.Id == c.GetProperty("id").GetGuid()))).ContactCount.Should().Be(2);
    }

    [Fact]
    public async Task Settings_round_trip_and_always_show_images()
    {
        using var api = await Client("ngoc@bluemail.test");
        var saved = await Data(await api.PutAsJsonAsync("/api/v1/me/settings", new { pageSize = 100, undoSendSeconds = 7, externalImages = "ALWAYS", keyboardShortcuts = true, inboxCategories = new[] { "SOCIAL" } }));
        saved.GetProperty("pageSize").GetInt32().Should().Be(100);
        saved.GetProperty("undoSendSeconds").GetInt32().Should().Be(10, "invalid values fall back to the default");
        saved.GetProperty("inboxCategories").EnumerateArray().Select(x => x.GetString()).Should().Equal("PRIMARY", "SOCIAL");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var m = await Receive("ngoc@bluemail.test", $"From: x@shop.test\nTo: ngoc@bluemail.test\nSubject: img {tag}\nMessage-ID: <i{tag}@t>\nContent-Type: text/html\n\n<p>Hi</p><img src=\"https://cdn.shop.test/a.png\">\n");
        var body = await Data(await api.GetAsync($"/api/v1/mail/messages/{m.Message}/body"));
        body.GetProperty("remoteImagesShown").GetBoolean().Should().BeTrue();
        await api.PutAsJsonAsync("/api/v1/me/settings", new { externalImages = "ASK" });

        (await api.PutAsJsonAsync("/api/v1/me/profile", new { displayName = "Võ Bảo Ngọc", locale = "xx", timeZone = "Asia/Ho_Chi_Minh" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.PutAsJsonAsync("/api/v1/me/profile", new { displayName = "Võ Bảo Ngọc", locale = "vi", timeZone = "Asia/Ho_Chi_Minh", hourCycle = "h23", weekStart = 1 }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Trash_and_spam_are_emptied_after_30_days_except_legal_hold()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var api = await Client("mei@bluemail.test");
        var old = await Receive("mei@bluemail.test", $"From: x@shop.test\nTo: mei@bluemail.test\nSubject: old {tag}\nMessage-ID: <o{tag}@t>\n\nx\n");
        var held = await Receive("mei@bluemail.test", $"From: x@shop.test\nTo: mei@bluemail.test\nSubject: held {tag}\nMessage-ID: <h{tag}@t>\n\nx\n");
        var fresh = await Receive("mei@bluemail.test", $"From: x@shop.test\nTo: mei@bluemail.test\nSubject: fresh {tag}\nMessage-ID: <f{tag}@t>\n\nx\n");
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "trash", messageIds = new[] { old.Message, held.Message, fresh.Message } })).EnsureSuccessStatusCode();
        await Db(async db =>
        {
            var trash = await db.Labels.IgnoreQueryFilters().Where(l => l.SystemKey == "TRASH" && db.Messages.IgnoreQueryFilters().Any(m => m.Id == old.Message && m.MailboxId == l.MailboxId))
                .Select(l => l.Id).SingleAsync();
            var uids = await db.MessageLabels.IgnoreQueryFilters().Where(ml => ml.LabelId == trash && (ml.MessageId == old.Message || ml.MessageId == held.Message))
                .Select(ml => ml.ImapUid).ToListAsync();
            await db.Set<LabelChange>().IgnoreQueryFilters().Where(c => c.LabelId == trash && uids.Contains(c.ImapUid))
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.OccurredAt, DateTimeOffset.UtcNow.AddDays(-31)));
            return await db.Messages.IgnoreQueryFilters().Where(m => m.Id == held.Message).ExecuteUpdateAsync(m => m.SetProperty(x => x.LegalHoldCount, 1));
        });

        await using (var scope = stack.ApiScope())
        {
            await scope.ServiceProvider.GetRequiredService<PgMailStore>().PurgeTrashAndSpamAsync(MailMaintenanceWorker.TrashRetention, 10_000);
        }

        (await Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == old.Message))).ExpungedAt.Should().NotBeNull();
        (await Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == held.Message))).ExpungedAt.Should().BeNull("legal hold");
        (await Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == fresh.Message))).ExpungedAt.Should().BeNull("not 30 days yet");
        await Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == held.Message).ExecuteUpdateAsync(m => m.SetProperty(x => x.LegalHoldCount, 0)));
    }

    [Fact]
    public async Task Shared_contacts_can_be_read_by_the_colleague_only_and_never_changed()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        using var priya = await Client("priya@acme.test");
        var id = (await Data(await priya.PostAsJsonAsync("/api/v1/contacts", new { displayName = "Supplier " + tag, emails = new[] { new { value = $"s.{tag}@shop.test" } }, phones = Array.Empty<object>() }))).GetGuid();
        (await Data(await priya.PutAsJsonAsync("/api/v1/contacts/shares", new { email = "amara@acme.test", shared = true })))
            .GetProperty("mine").EnumerateArray().Should().ContainSingle(x => x.GetProperty("email").GetString() == "amara@acme.test");
        (await priya.PutAsJsonAsync("/api/v1/contacts/shares", new { email = "priya@acme.test", shared = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await priya.PutAsJsonAsync("/api/v1/contacts/shares", new { email = "thao@bluemail.test", shared = true })).StatusCode.Should().Be(HttpStatusCode.NotFound, "other organisations look unknown");

        using var amara = await Client("amara@acme.test");
        var owner = (await Data(await amara.GetAsync("/api/v1/contacts/shares"))).GetProperty("withMe").EnumerateArray()
            .Single(x => x.GetProperty("email").GetString() == "priya@acme.test").GetProperty("userId").GetGuid();
        var list = await Data(await amara.GetAsync($"/api/v1/contacts?owner={owner}&q={tag}"));
        list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayName").GetString()).Should().Equal("Supplier " + tag);
        (await amara.PutAsJsonAsync($"/api/v1/contacts/{id}", new { displayName = "x", emails = Array.Empty<object>(), phones = Array.Empty<object>() })).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "read only");

        // Nobody else, and nothing after the share ends.
        using var joao = await Client("joao@acme.test");
        (await joao.GetAsync($"/api/v1/contacts?owner={owner}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await Data(await priya.PutAsJsonAsync("/api/v1/contacts/shares", new { email = "amara@acme.test", shared = false }));
        (await amara.GetAsync($"/api/v1/contacts?owner={owner}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
