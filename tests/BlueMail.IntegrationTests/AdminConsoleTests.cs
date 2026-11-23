using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlueMail.Domain.Admin;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 9: domains wizard, users, groups, policies, routing, operations, tenants (PROMPT 7.1–7.4, 7.6).</summary>
[Collection(StackCollection.Name)]
public class AdminConsoleTests(StackFixture stack)
{
    private async Task<HttpClient> Client(string email, string? password = null) =>
        OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, password ?? StackFixture.UserPassword));

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

    private async Task<ResolvedRecipient> Resolve(string address)
    {
        await using var scope = stack.ApiScope();
        return await scope.ServiceProvider.GetRequiredService<RecipientResolver>().ResolveAsync(address, 0, default);
    }

    [Fact]
    public async Task Domain_wizard_verifies_by_txt_checks_records_and_rotates_dkim()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var domain = $"khach-{tag}.test";
        using var admin = await Client("lan@bluemail.test");
        using var user = await Client("minh@bluemail.test");
        (await user.GetAsync("/api/v1/admin/domains")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var id = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/domains", new { domain }))).GetGuid();
        (await admin.PostAsJsonAsync("/api/v1/admin/domains", new { domain = domain.ToUpperInvariant() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var plan = await Data(await admin.GetAsync($"/api/v1/admin/domains/{id}/records"));
        var records = plan.GetProperty("records").EnumerateArray().ToList();
        var token = records.Single(r => r.GetProperty("kind").GetString() == "VERIFICATION").GetProperty("expected").GetString()!;
        var dkim = records.Single(r => r.GetProperty("kind").GetString() == "DKIM");
        plan.GetProperty("domain").GetProperty("verified").GetBoolean().Should().BeFalse();

        // Unverified: no mail accepted for it.
        await AddUserAsync(admin, $"giam.doc@{domain}");
        (await Resolve($"giam.doc@{domain}")).Status.Should().Be(RecipientStatus.NotOurDomain);

        // The admin publishes the records; the check sees them.
        stack.Dns.Records[("TXT", domain)] = [token, "v=spf1 include:_spf.bluemail.test -all"];
        stack.Dns.Records[("MX", domain)] = ["mx.bluemail.test"];
        stack.Dns.Records[("TXT", dkim.GetProperty("name").GetString()!)] = [dkim.GetProperty("expected").GetString()!];
        var check = await Data(await admin.GetAsync($"/api/v1/admin/domains/{id}/records?check=true"));
        check.GetProperty("domain").GetProperty("verified").GetBoolean().Should().BeTrue();
        string State(JsonElement c, string kind) => c.GetProperty("records").EnumerateArray().First(r => r.GetProperty("kind").GetString() == kind).GetProperty("state").GetString()!;
        (State(check, "MX"), State(check, "SPF"), State(check, "DKIM"), State(check, "DMARC")).Should().Be(("OK", "OK", "OK", "MISSING"));
        (await Resolve($"giam.doc@{domain}")).Status.Should().Be(RecipientStatus.Ok);

        // DKIM rotation: a pending key becomes active once its record is in DNS; the old key retires.
        var pending = await Data(await admin.PostAsync($"/api/v1/admin/domains/{id}/dkim/rotate", null));
        (await admin.PostAsync($"/api/v1/admin/domains/{id}/dkim/rotate", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var after = await Data(await admin.GetAsync($"/api/v1/admin/domains/{id}/records"));
        var newRecord = after.GetProperty("records").EnumerateArray().First(r => r.GetProperty("name").GetString()!.StartsWith(pending.GetProperty("selector").GetString()!, StringComparison.Ordinal));
        stack.Dns.Records[("TXT", newRecord.GetProperty("name").GetString()!)] = [newRecord.GetProperty("expected").GetString()!];
        var rotated = await Data(await admin.GetAsync($"/api/v1/admin/domains/{id}/records?check=true"));
        var keys = rotated.GetProperty("domain").GetProperty("dkimKeys").EnumerateArray().Select(k => k.GetProperty("status").GetString()).ToList();
        keys.Should().BeEquivalentTo(["ACTIVE", "RETIRING"]);

        // MTA-STS policy is served for the verified domain.
        var policy = await stack.Api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/.well-known/mta-sts.txt") { Headers = { Host = "mta-sts." + domain } });
        (await policy.Content.ReadAsStringAsync()).Should().Contain("version: STSv1").And.Contain("mx: mx.bluemail.test");

        // Domain alias and catch-all.
        var alias = $"alias-{tag}.test";
        var aliasId = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/domains", new { domain = alias, aliasOfDomainId = id }))).GetGuid();
        await Db(db => db.Domains.IgnoreQueryFilters().Where(d => d.Id == aliasId).ExecuteUpdateAsync(d => d.SetProperty(x => x.VerifiedAt, DateTimeOffset.UtcNow)));
        (await Resolve($"giam.doc@{alias}")).Should().Match<ResolvedRecipient>(r => r.Status == RecipientStatus.Ok && r.Address == $"giam.doc@{alias}");
        (await admin.PutAsJsonAsync($"/api/v1/admin/domains/{id}/catch-all", new { target = "someone@doitac.test" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/admin/domains/{id}/catch-all", new { target = $"giam.doc@{domain}" })).EnsureSuccessStatusCode();
        (await Resolve($"nobody@{domain}")).Kind.Should().Be("catch-all");
        (await admin.DeleteAsync($"/api/v1/admin/domains/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "users and an alias domain still use it");
    }

    private static async Task<Guid> AddUserAsync(HttpClient admin, string email) =>
        (await Data(await admin.PostAsJsonAsync("/api/v1/admin/users", new { email, displayName = "Giám đốc", password = "Very-Long-Password-1", isTenantAdmin = false, quotaBytes = (long?)null })))
        .GetProperty("id").GetGuid();

    [Fact]
    public async Task Users_csv_import_reports_errors_then_creates_and_admin_actions_work()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await Client("lan@bluemail.test");
        var csv = $"Email,Name,Admin,Quota,Org unit\r\nnv1.{tag}@bluemail.test,Nhân viên 1,no,5,Kế toán\r\nbad-address,X,,,\r\nnv1.{tag}@bluemail.test,Trùng,,,\r\nnv2.{tag}@other.test,Y,,,\r\n";
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "users.csv" } };
        var rows = await Data(await admin.PostAsync("/api/v1/admin/users/import/parse", form));
        rows.GetArrayLength().Should().Be(4);
        var check = await Data(await admin.PostAsJsonAsync("/api/v1/admin/users/import", new { rows, commit = true }));
        check.GetProperty("created").GetInt32().Should().Be(0, "nothing is created while a row is wrong");
        check.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString()))
            .Should().BeEquivalentTo([(2, "invalid_email"), (3, "duplicate_in_file"), (4, "domain_not_in_tenant")]);

        // The admin corrects the table and sends it again.
        var fixedRows = new[] { new { email = $"nv1.{tag}@bluemail.test", displayName = "Nhân viên 1", password = (string?)null, isTenantAdmin = false, quotaGb = (long?)5, orgUnit = "Kế toán" } };
        var done = await Data(await admin.PostAsJsonAsync("/api/v1/admin/users/import", new { rows = fixedRows, commit = true }));
        done.GetProperty("created").GetInt32().Should().Be(1);
        var password = done.GetProperty("passwords")[0].GetProperty("password").GetString()!;

        var id = await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == $"nv1.{tag}@bluemail.test").Select(u => u.Id).SingleAsync());
        (await Db(db => db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == id))).QuotaBytes.Should().Be(5L * 1024 * 1024 * 1024);

        // Reset password, alias, change address (old one stays as alias), details with sign-ins.
        var temp = (await Data(await admin.PostAsync($"/api/v1/admin/users/{id}/reset-password", null))).GetString()!;
        temp.Should().NotBe(password).And.HaveLength(16);
        (await admin.PostAsJsonAsync($"/api/v1/admin/users/{id}/aliases", new { email = $"alias.{tag}@bluemail.test" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/admin/users/{id}/aliases", new { email = "lan@bluemail.test" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/admin/users/{id}/address", new { email = $"moi.{tag}@bluemail.test" })).EnsureSuccessStatusCode();
        var detail = await Data(await admin.GetAsync($"/api/v1/admin/users/{id}"));
        detail.GetProperty("email").GetString().Should().Be($"moi.{tag}@bluemail.test");
        detail.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()).Should().Contain([$"nv1.{tag}@bluemail.test", $"alias.{tag}@bluemail.test"]);
        (await Resolve($"nv1.{tag}@bluemail.test")).Status.Should().Be(RecipientStatus.Ok, "mail to the old address still arrives");

        // Delete, moving the mail to another user first.
        await using (var scope = stack.ApiScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
            var r = await Resolve($"moi.{tag}@bluemail.test");
            var raw = Encoding.UTF8.GetBytes($"From: a@doitac.test\r\nTo: moi.{tag}@bluemail.test\r\nSubject: keep {tag}\r\nMessage-ID: <k{tag}@t>\r\n\r\nx\r\n");
            await BlueMail.Application.Features.Sending.OutgoingDispatcher.DeliverRawAsync(store, r.TenantId, raw,
                new MailboxDelivery(r.MailboxIds[0], [Domain.Mail.SystemLabels.Inbox]), default);
        }

        var hoa = await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "hoa@bluemail.test").Select(u => u.Id).SingleAsync());
        (await Data(await admin.DeleteAsync($"/api/v1/admin/users/{id}?transferTo={hoa}"))).GetInt32().Should().Be(1);
        (await Resolve($"moi.{tag}@bluemail.test")).Status.Should().Be(RecipientStatus.Unknown);
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.Subject == "keep " + tag && db.Mailboxes.Any(b => b.Id == m.MailboxId && b.OwnerId == hoa))))
            .Should().Be(1);
    }

    [Fact]
    public async Task Groups_post_permissions_external_members_shared_mailboxes()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await Client("lan@bluemail.test");
        var gid = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/groups", new
        {
            email = $"duan-{tag}@bluemail.test", name = "Dự án", description = (string?)null, whoCanPost = "MEMBERS", allowExternal = false, moderated = false,
        }))).GetGuid();
        (await admin.PostAsJsonAsync($"/api/v1/admin/groups/{gid}/members", new { email = "tuan@bluemail.test", role = "MANAGER" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/admin/groups/{gid}/members", new { email = "khach@doitac.test", role = "MEMBER" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/admin/groups/{gid}/members", new { email = "no.such@bluemail.test", role = "MEMBER" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var r = await Resolve($"duan-{tag}@bluemail.test");
        r.ExternalMembers.Should().Equal("khach@doitac.test");
        await using var scope = stack.ApiScope();
        var posting = scope.ServiceProvider.GetRequiredService<BlueMail.Application.Features.Admin.Directory.GroupPosting>();
        (await posting.CheckAsync(r.TenantId, gid, "tuan@bluemail.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Allow);
        (await posting.CheckAsync(r.TenantId, gid, "khach@doitac.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Allow, "an external member");
        (await posting.CheckAsync(r.TenantId, gid, "minh@bluemail.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Refuse, "not a member");
        (await posting.CheckAsync(r.TenantId, gid, "stranger@doitac.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Refuse);
        await admin.PutAsJsonAsync($"/api/v1/admin/groups/{gid}", new { email = "", name = "Dự án", description = (string?)null, whoCanPost = "ANYONE", allowExternal = true, moderated = true });
        (await posting.CheckAsync(r.TenantId, gid, "stranger@doitac.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Moderate);
        (await posting.CheckAsync(r.TenantId, gid, "tuan@bluemail.test", default)).Should().Be(BlueMail.Application.Features.Admin.Directory.PostingDecision.Allow, "managers skip moderation");

        var sid = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/shared-mailboxes", new { email = $"hotro-{tag}@bluemail.test", name = "Hỗ trợ" }))).GetGuid();
        var mei = await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "mei@bluemail.test").Select(u => u.Id).SingleAsync());
        (await admin.PutAsJsonAsync($"/api/v1/admin/shared-mailboxes/{sid}/members", new { userId = mei, canRead = true, canSendAs = true, canSendOnBehalf = false }))
            .EnsureSuccessStatusCode();
        (await Resolve($"hotro-{tag}@bluemail.test")).Kind.Should().Be("shared");
        using var meiApi = await Client("mei@bluemail.test");
        var from = await Data(await meiApi.GetAsync("/api/v1/mail/compose/from-options"));
        from.EnumerateArray().Should().Contain(f => f.GetProperty("address").GetString() == $"hotro-{tag}@bluemail.test");
    }

    [Fact]
    public async Task Policies_are_typed_and_read_where_they_act()
    {
        using var admin = await Client("lan@bluemail.test");
        var list = await Data(await admin.GetAsync("/api/v1/admin/policies"));
        list.EnumerateArray().Select(p => p.GetProperty("key").GetString()).Should().Contain(["max_message_size_mb", "require_2fa", "outbound_footer"]);
        (await admin.PutAsJsonAsync("/api/v1/admin/policies/max_message_size_mb", new { value = "big" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/v1/admin/policies/no_such_key", new { value = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Blocked file types stop uploads; the external banner flag reaches the web.
        (await admin.PutAsJsonAsync("/api/v1/admin/policies/blocked_extensions", new { value = new[] { "exe", ".js" } })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/v1/admin/policies/external_sender_banner", new { value = false })).EnsureSuccessStatusCode();
        try
        {
            using var user = await Client("ngoc@bluemail.test");
            var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads");
            upload.Headers.Add("Tus-Resumable", "1.0.0");
            upload.Headers.Add("Upload-Length", "10");
            upload.Headers.Add("Upload-Metadata", "filename " + Convert.ToBase64String("setup.EXE"u8.ToArray()));
            (await user.SendAsync(upload)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Data(await user.GetAsync("/api/v1/me"))).GetProperty("externalSenderBanner").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/admin/policies/blocked_extensions", new { value = (string?)null });
            await admin.PutAsJsonAsync("/api/v1/admin/policies/external_sender_banner", new { value = (string?)null });
        }

        // Routing rules: an inbound rule needs a condition; outbound rules only BCC or reject.
        (await admin.PostAsJsonAsync("/api/v1/admin/routing-rules", new
        {
            name = "x", direction = "OUTBOUND", condition = new { subjectContains = "x" }, action = new { type = "REDIRECT", address = "a@bluemail.test" }, isEnabled = true,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var rule = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/routing-rules", new
        {
            name = "Lưu trữ thư gửi kế toán", direction = "INBOUND", condition = new { recipientContains = "quang@" }, action = new { type = "BCC", address = "lan@bluemail.test" },
            isEnabled = true,
        }))).GetGuid();
        await using (var scope = stack.ApiScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<BlueMail.Application.Features.Admin.Policies.RoutingEngine>();
            var tenant = (await Resolve("quang@bluemail.test")).TenantId;
            var actions = await engine.EvaluateAsync(tenant, Domain.Tenants.RoutingDirection.Inbound,
                new BlueMail.Application.Features.Admin.Policies.RoutedMessage("x@doitac.test", ["quang@bluemail.test"], "hi", false), default);
            actions.Should().ContainSingle(a => a.Type == "BCC" && a.Address == "lan@bluemail.test");
        }

        (await admin.DeleteAsync($"/api/v1/admin/routing-rules/{rule}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Trace_queue_suppressions_health_and_reports()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (tenant, _) = await Db(async db => (await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "lan@bluemail.test").Select(u => u.TenantId).SingleAsync(), 0));
        await Db(async db =>
        {
            db.MailLog.Add(new MailLogEntry { TenantId = tenant, Direction = MailDirection.Inbound, RfcMessageId = $"t{tag}@x", EnvelopeFrom = $"s.{tag}@doitac.test",
                Rcpt = "minh@bluemail.test", Size = 100, Status = MailLogStatus.Delivered, SpamScore = 7, OccurredAt = DateTimeOffset.UtcNow });
            db.Suppressions.Add(new Domain.Mail.Suppression { TenantId = tenant, Address = $"gone.{tag}@x.test", Reason = Domain.Mail.SuppressionReason.HardBounce,
                CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        using var admin = await Client("lan@bluemail.test");
        using var user = await Client("minh@bluemail.test");
        (await user.GetAsync("/api/v1/admin/trace")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var trace = await Data(await admin.GetAsync($"/api/v1/admin/trace?sender=s.{tag}"));
        var row = trace.GetProperty("items").EnumerateArray().Single();
        var detail = await Data(await admin.GetAsync($"/api/v1/admin/trace/{row.GetProperty("id").GetInt64()}"));
        detail.GetProperty("row").GetProperty("spamScore").GetDouble().Should().Be(7);

        var supp = await Data(await admin.GetAsync($"/api/v1/admin/suppressions?search=gone.{tag}"));
        var sid = supp.GetProperty("items")[0].GetProperty("id").GetInt64();
        (await admin.DeleteAsync($"/api/v1/admin/suppressions/{sid}")).EnsureSuccessStatusCode();
        (await Data(await admin.GetAsync($"/api/v1/admin/suppressions?search=gone.{tag}"))).GetProperty("total").GetInt32().Should().Be(0);

        (await Data(await admin.GetAsync("/api/v1/admin/queue?status=ALL"))).GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        var health = await Data(await admin.GetAsync("/api/v1/admin/health?days=30"));
        health.GetProperty("days").GetInt32().Should().Be(30);

        var report = await Data(await admin.GetAsync("/api/v1/admin/reports/spam_rate"));
        report.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Contain("spam_percent");
        (await admin.GetAsync("/api/v1/admin/reports/nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var xlsx = await admin.GetAsync("/api/v1/admin/reports/users_without_2fa/xlsx");
        xlsx.EnsureSuccessStatusCode();
        using var zip = new System.IO.Compression.ZipArchive(await xlsx.Content.ReadAsStreamAsync());
        var sheet = await new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEndAsync();
        sheet.Should().Contain("Người dùng chưa bật xác minh hai bước").And.Contain("Đầy đủ:", "every export says whether it is complete").And.Contain("lan@bluemail.test");
    }

    [Fact]
    public async Task Platform_admin_manages_tenants_and_support_sign_in_is_audited_and_announced()
    {
        // Another test turns on two-step verification for the platform administrator; this one signs in with the password only.
        await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == StackFixture.PlatformAdmin).ExecuteUpdateAsync(u => u.SetProperty(x => x.TwoFactorEnabled, false).SetProperty(x => x.MustChangePassword, false)));
        using var platform = await Client(StackFixture.PlatformAdmin, StackFixture.PlatformAdminPassword);
        using var tenantAdmin = await Client("lan@bluemail.test");
        var tenants = await Data(await platform.GetAsync("/api/v1/admin/tenants"));
        var bm = tenants.GetProperty("items").EnumerateArray().First(t => t.GetProperty("code").GetString() == "bluemail").GetProperty("id").GetGuid();
        (await tenantAdmin.GetAsync($"/api/v1/admin/tenants/{bm}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var detail = await Data(await platform.GetAsync($"/api/v1/admin/tenants/{bm}"));
        detail.GetProperty("usage").GetProperty("users").GetInt32().Should().BeGreaterThan(5);

        var anna = await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "anna@bluemail.test").Select(u => u.Id).SingleAsync());
        (await platform.PostAsJsonAsync($"/api/v1/admin/tenants/{bm}/support-signin", new { userId = anna, reason = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var link = (await Data(await platform.PostAsJsonAsync($"/api/v1/admin/tenants/{bm}/support-signin", new { userId = anna, reason = "Ticket #123: lost mail" }))).GetString()!;
        link.Should().StartWith(StackFixture.IdentityUrl + "/account/support?ticket=");
        (await Db(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "support.signin.start" && a.EntityId == anna.ToString()))).Should().BeTrue();
        (await Db(db => db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Subject!.StartsWith("Đăng nhập hộ để hỗ trợ: anna@bluemail.test")))).Should().BeTrue(
            "the tenant's administrators are told");

        // Suspension mode: 450 (retry) by default, 550 when the platform says so.
        var body = new
        {
            name = detail.GetProperty("name").GetString(), plan = detail.GetProperty("plan").GetString(), userLimit = 0, storageQuotaBytes = 0L, outboundDailyLimit = 0,
            defaultLocale = "vi", defaultTimeZone = "Asia/Ho_Chi_Minh", suspendTemporary = false, branding = "{}",
        };
        (await platform.PutAsJsonAsync($"/api/v1/admin/tenants/{bm}", body)).EnsureSuccessStatusCode();
        (await Db(db => db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == bm))).SuspendTemporary.Should().BeFalse();
        (await platform.PutAsJsonAsync($"/api/v1/admin/tenants/{bm}", body with { suspendTemporary = true })).EnsureSuccessStatusCode();
    }
}
