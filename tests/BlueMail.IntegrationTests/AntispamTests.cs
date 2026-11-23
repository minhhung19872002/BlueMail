using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlueMail.Antispam;
using BlueMail.Application.Features.Antispam;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 6: learning from users, phishing reports, tenant spam settings, quarantine, greylisting.</summary>
[Collection(StackCollection.Name)]
public class AntispamTests(StackFixture stack)
{
    private async Task<(Guid Tenant, Guid Mailbox, Guid User)> MailboxOf(string email)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.PrimaryEmail == email);
        return (user.TenantId, await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == user.Id).Select(m => m.Id).SingleAsync(), user.Id);
    }

    private async Task<Guid> Deliver(string to, string raw)
    {
        var (tenant, mailbox, _) = await MailboxOf(to);
        await using var scope = stack.ApiScope();
        var s = new MemoryStream(Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n")));
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        s.Position = 0;
        return (await scope.ServiceProvider.GetRequiredService<PgMailStore>()
            .DeliverAsync(new DeliveryRequest(tenant, s, facts, [new MailboxDelivery(mailbox, [SystemLabels.Inbox])], DateTimeOffset.UtcNow)))[0].MessageId;
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

    [Fact]
    public async Task Report_spam_trains_the_users_model_once_and_not_spam_undoes_it()
    {
        var (tenant, _, user) = await MailboxOf("yen@bluemail.test");
        using var api = await Client("yen@bluemail.test");
        var spamIds = new List<Guid>();
        var hamIds = new List<Guid>();
        for (var i = 0; i < 22; i++)
        {
            spamIds.Add(await Deliver("yen@bluemail.test", $"From: promo{i}@deals.test\nSubject: Siêu khuyến mãi {i}\nMessage-ID: <s{i}-{Guid.NewGuid()}@t>\n\nGiảm giá sốc voucher miễn phí bấm ngay {i}\n"));
            hamIds.Add(await Deliver("yen@bluemail.test", $"From: ketoan{i}@doitac.test\nSubject: Đối chiếu công nợ {i}\nMessage-ID: <h{i}-{Guid.NewGuid()}@t>\n\nGửi chị bảng đối chiếu công nợ quý {i}\n"));
        }

        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "spam", messageIds = spamIds })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "spam", messageIds = spamIds })).EnsureSuccessStatusCode(); // twice: no double count
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "notSpam", messageIds = hamIds })).EnsureSuccessStatusCode();

        var totals = await Db(db => db.BayesTotals.AsNoTracking().SingleAsync(t => t.Scope == (short)BayesScope.User && t.ScopeId == user));
        totals.SpamMessages.Should().Be(22);
        totals.HamMessages.Should().Be(22);

        // The model now separates the two kinds for this user.
        await using (var scope = stack.ApiScope())
        {
            var bayes = scope.ServiceProvider.GetRequiredService<BayesStore>();
            var spamTokens = Tokenizer.Tokens("Siêu khuyến mãi voucher miễn phí bấm ngay").ToList();
            var models = await bayes.LoadAsync(tenant, user, spamTokens, default);
            models.Should().Contain(m => m.Scope == BayesScope.User);
            Bayes.Classify(spamTokens, models)!.Probability.Should().BeGreaterThan(0.9);
        }

        // Changing one's mind moves the lesson instead of adding a second one.
        (await api.PostAsJsonAsync("/api/v1/mail/actions", new { action = "notSpam", messageIds = spamIds.Take(2) })).EnsureSuccessStatusCode();
        totals = await Db(db => db.BayesTotals.AsNoTracking().SingleAsync(t => t.Scope == (short)BayesScope.User && t.ScopeId == user));
        (totals.SpamMessages, totals.HamMessages).Should().Be((20, 24));
    }

    [Fact]
    public async Task Phishing_report_goes_to_spam_and_reaches_the_security_mailbox_settings_are_admin_only()
    {
        using var admin = await Client("lan@bluemail.test");
        using var user = await Client("ngoc@bluemail.test");
        (await user.GetAsync("/api/v1/admin/spam/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var external = await admin.PutAsJsonAsync("/api/v1/admin/spam/settings",
            new { spamThreshold = 5.0, rejectThreshold = 15.0, greylisting = false, suspiciousAttachments = "SPAM", phishingReportAddress = "soc@elsewhere.test" });
        external.StatusCode.Should().Be(HttpStatusCode.BadRequest, "reports never leave the organisation");
        (await admin.PutAsJsonAsync("/api/v1/admin/spam/settings",
            new { spamThreshold = 6.0, rejectThreshold = 5.0, greylisting = false, suspiciousAttachments = "SPAM", phishingReportAddress = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "reject must be above spam");
        (await admin.PutAsJsonAsync("/api/v1/admin/spam/settings",
            new { spamThreshold = 5.0, rejectThreshold = 15.0, greylisting = false, suspiciousAttachments = "QUARANTINE", phishingReportAddress = "tuan@bluemail.test" }))
            .EnsureSuccessStatusCode();
        var settings = await Data(await admin.GetAsync("/api/v1/admin/spam/settings"));
        settings.GetProperty("suspiciousAttachments").GetString().Should().Be("QUARANTINE");
        settings.GetProperty("lists").EnumerateArray().Should().OnlyContain(l => !l.GetProperty("enabled").GetBoolean(), "block lists are off by default");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var id = await Deliver("ngoc@bluemail.test", $"From: \"IT Support\" <it@bluemaiI.test>\nSubject: Verify your account {tag}\nMessage-ID: <{tag}@t>\n\nYour password expires today\n");
        (await user.PostAsJsonAsync("/api/v1/mail/actions", new { action = "reportPhishing", messageIds = new[] { id } })).EnsureSuccessStatusCode();

        var labels = await Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters() join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                     where ml.MessageId == id select l.SystemKey).ToListAsync());
        labels.Should().Contain(SystemLabels.Spam).And.NotContain(SystemLabels.Inbox);
        var (_, socMailbox, _) = await MailboxOf("tuan@bluemail.test");
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.MailboxId == socMailbox && m.Subject == "[Phishing report] Verify your account " + tag)))
            .Should().Be(1);

        await admin.PutAsJsonAsync("/api/v1/admin/spam/settings",
            new { spamThreshold = 5.0, rejectThreshold = 15.0, greylisting = false, suspiciousAttachments = "SPAM", phishingReportAddress = (string?)null });
    }

    [Fact]
    public async Task Quarantined_message_is_released_once_to_its_recipients()
    {
        var (tenant, mailbox, _) = await MailboxOf("mei@bluemail.test");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var raw = Encoding.UTF8.GetBytes($"From: a@partner.test\r\nTo: mei@bluemail.test\r\nSubject: held {tag}\r\nMessage-ID: <{tag}@t>\r\n\r\nx\r\n");
        await using (var scope = stack.ApiScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<BlobStore>();
            var sha = await BlobStore.HashAsync(new MemoryStream(raw), default);
            await blobs.PutAsync(tenant, sha, new MemoryStream(raw), default);
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            db.Quarantine.Add(new QuarantinedMessage
            {
                TenantId = tenant, BlobSha256 = sha, Size = raw.Length, Recipients = JsonSerializer.Serialize(new[] { mailbox }), Subject = "held " + tag,
                FromAddress = "a@partner.test", Reason = "suspicious_attachment", Status = QuarantineStatus.Held,
            });
            await db.SaveChangesAsync();
        }

        using var admin = await Client("lan@bluemail.test");
        using var user = await Client("mei@bluemail.test");
        (await user.GetAsync("/api/v1/admin/quarantine")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var list = await Data(await admin.GetAsync("/api/v1/admin/quarantine?pageSize=200"));
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("subject").GetString() == "held " + tag);
        var itemId = item.GetProperty("id").GetGuid();

        var results = await Task.WhenAll(admin.PostAsync($"/api/v1/admin/quarantine/{itemId}/release", null), admin.PostAsync($"/api/v1/admin/quarantine/{itemId}/release", null));
        results.Count(r => r.IsSuccessStatusCode).Should().Be(1, "two administrators releasing at once deliver once");
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.MailboxId == mailbox && m.Subject == "held " + tag))).Should().Be(1);
    }

    [Fact]
    public async Task Greylisting_defers_a_new_triplet_and_lets_the_retry_through()
    {
        var (tenant, _, _) = await MailboxOf("hoa@bluemail.test");
        var ip = IPAddress.Parse("198.51.100." + Random.Shared.Next(1, 250));
        var sender = Guid.NewGuid().ToString("N")[..8] + "@sender.test";
        await using var scope = stack.ApiScope();
        var grey = scope.ServiceProvider.GetRequiredService<Greylisting>();
        (await grey.PassAsync(tenant, ip, sender, "hoa@bluemail.test", default)).Should().BeFalse("first attempt");
        (await grey.PassAsync(tenant, ip, sender, "hoa@bluemail.test", default)).Should().BeFalse("too early");
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        await db.Greylist.Where(g => g.Sender == sender).ExecuteUpdateAsync(g => g.SetProperty(x => x.FirstSeen, DateTimeOffset.UtcNow.AddMinutes(-6)));
        db.ChangeTracker.Clear();
        (await grey.PassAsync(tenant, ip, sender, "hoa@bluemail.test", default)).Should().BeTrue("retried after the delay");
        (await grey.PassAsync(tenant, IPAddress.Parse(ip.ToString()[..ip.ToString().LastIndexOf('.')] + ".7"), sender, "hoa@bluemail.test", default))
            .Should().BeTrue("same /24 network");
    }
}
