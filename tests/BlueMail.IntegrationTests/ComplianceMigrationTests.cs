using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Features.Compliance;
using BlueMail.Domain.Compliance;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 10: import (IMAP, mbox), legal hold, retention, eDiscovery, exports (PROMPT 7.5, 7.7).</summary>
[Collection(StackCollection.Name)]
public class ComplianceMigrationTests(StackFixture stack)
{
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

    private async Task<(Guid Tenant, Guid User, Guid Mailbox)> Who(string email) => await Db(async db =>
    {
        var u = await db.Users.IgnoreQueryFilters().Where(x => x.PrimaryEmail == email).Select(x => new { x.Id, x.TenantId }).SingleAsync();
        var mb = await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == u.Id).Select(m => m.Id).SingleAsync();
        return (u.TenantId, u.Id, mb);
    });

    private static async Task<JsonElement> WaitJob(HttpClient api, string listUrl, Guid id)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = (await Data(await api.GetAsync(listUrl))).EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id);
            var status = job.GetProperty("status").GetString();
            if (status is "DONE" or "FAILED" or "CANCELLED")
            {
                return job;
            }

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(3), $"job {id} is {status}");
            await Task.Delay(500);
        }
    }

    private static (int Code, string Out) Tools(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "exec", "bluemail-dev-tools-1" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static readonly object Imap = new { host = "127.0.0.1", port = 55993, tls = "IMPLICIT", username = "nguon@doitac.test", password = "Doitac-Imap-Dev-1" };

    [Fact]
    public async Task Imap_import_keeps_folders_flags_dates_and_never_imports_twice()
    {
        Tools("python", "/work/tests/interop/imap_source.py", "populate").Code.Should().Be(0, "the dev compose (doitac + tools) must be running");
        using var api = await Client("thao@bluemail.test");
        var (tenant, _, mailbox) = await Who("thao@bluemail.test");

        var test = await Data(await api.PostAsJsonAsync("/api/v1/me/imports/test", Imap));
        test.GetProperty("ok").GetBoolean().Should().BeTrue(test.ToString());
        test.GetProperty("messages").GetInt64().Should().Be(9);
        var wrong = await Data(await api.PostAsJsonAsync("/api/v1/me/imports/test", new { host = "127.0.0.1", port = 55993, tls = "IMPLICIT", username = "nguon@doitac.test", password = "x" }));
        wrong.GetProperty("error").GetString().Should().Be("import_auth_failed");
        (await api.PostAsJsonAsync("/api/v1/me/imports", new { host = "127.0.0.1", port = 25, tls = "IMPLICIT", username = "a", password = "b" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "only IMAP ports (anti-SSRF)");

        var id = (await Data(await api.PostAsJsonAsync("/api/v1/me/imports", Imap))).GetGuid();
        (await api.PostAsJsonAsync("/api/v1/me/imports", Imap)).StatusCode.Should().Be(HttpStatusCode.Conflict, "one import per mailbox at a time");
        var job = await WaitJob(api, "/api/v1/me/imports", id);
        job.GetProperty("status").GetString().Should().Be("DONE", job.ToString());
        (job.GetProperty("imported").GetInt32(), job.GetProperty("skipped").GetInt32()).Should().Be((8, 1), "the \\Deleted message is not carried over");
        (await Db(db => db.ImportJobs.IgnoreQueryFilters().Where(j => j.Id == id).Select(j => j.PasswordCipher).SingleAsync())).Should().BeNull("the password is dropped");

        await using (var scope = stack.ApiScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            var msgs = await db.Messages.IgnoreQueryFilters().Where(m => m.MailboxId == mailbox && m.ExpungedAt == null && m.RfcMessageId!.EndsWith("@khachhang.test"))
                .Select(m => new
                {
                    m.Subject, m.Seen, m.Flagged, m.Answered, m.ReceivedAt, m.Size,
                    Labels = db.MessageLabels.IgnoreQueryFilters().Where(ml => ml.MessageId == m.Id)
                        .Join(db.Labels.IgnoreQueryFilters(), ml => ml.LabelId, l => l.Id, (ml, l) => l.SystemKey ?? l.Name).ToList(),
                }).ToListAsync();
            msgs.Should().HaveCount(8);
            var contract = msgs.Single(m => m.Subject == "Hợp đồng số 12");
            (contract.Seen, contract.Flagged).Should().Be((true, true));
            contract.Labels.Should().Contain([SystemLabels.Inbox, SystemLabels.Starred]);
            msgs.Single(m => m.Subject == "Chưa đọc").Seen.Should().BeFalse();
            var sent = msgs.Single(m => m.Subject == "Re: Báo giá");
            (sent.Answered, sent.Labels.Contains(SystemLabels.Sent), sent.Labels.Contains(SystemLabels.Inbox)).Should().Be((true, true, false));
            msgs.Single(m => m.Subject == "Báo cáo Q4").Labels.Should().Contain("Q4");
            msgs.Single(m => m.Subject == "Hóa đơn GTGT").Labels.Should().Contain("Hóa đơn & chứng từ");
            msgs.Single(m => m.Subject == "Báo giá tháng 11").ReceivedAt.Year.Should().Be(2025, "INTERNALDATE is kept");
            msgs.Single(m => m.Subject == "Tệp lớn").Size.Should().BeGreaterThan(4_000_000);
            var q4 = await db.Labels.IgnoreQueryFilters().SingleAsync(l => l.MailboxId == mailbox && l.Name == "Q4");
            (await db.Labels.IgnoreQueryFilters().SingleAsync(l => l.Id == q4.ParentId)).Name.Should().Be("Dự án", "nested folders become nested labels");
        }

        // Imported mail runs no filters or auto-replies: no outgoing mail was produced.
        (await Db(db => db.OutboundMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == tenant && o.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5)
                                                                              && o.EnvelopeFrom.StartsWith("thao")))).Should().Be(0);

        // Running it again imports nothing twice.
        var again = (await Data(await api.PostAsJsonAsync("/api/v1/me/imports", Imap))).GetGuid();
        var second = await WaitJob(api, "/api/v1/me/imports", again);
        (second.GetProperty("imported").GetInt32(), second.GetProperty("skipped").GetInt32()).Should().Be((0, 9));
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.MailboxId == mailbox && m.ExpungedAt == null && m.RfcMessageId!.EndsWith("@khachhang.test"))))
            .Should().Be(8);
    }

    private async Task<Guid> UploadAsync(string email, string fileName, byte[] content)
    {
        var (tenant, user, _) = await Who(email);
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<UploadStore>();
        var up = new Upload
        {
            Id = Guid.NewGuid(), TenantId = tenant, UserId = user, FileName = fileName, Length = content.Length, Offset = content.Length, Chunks = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), ScanStatus = "Clean",
        };
        await store.PutChunkAsync(tenant, up.Id, 0, content, default);
        db.Uploads.Add(up);
        await db.SaveChangesAsync();
        return up.Id;
    }

    [Fact]
    public async Task Mbox_import_reads_takeout_labels_and_unquotes_from_lines()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var file = new MemoryStream();
        var body1 = $"X-Gmail-Labels: Inbox,Starred,\"Khách hàng, VIP\",Unread\r\nFrom: a@x.test\r\nSubject: mbox1 {tag}\r\nMessage-ID: <m1{tag}@x>\r\n\r\nHello\r\nFrom the start\r\n>From quoted\r\n";
        var body2 = $"Status: RO\r\nX-Status: A\r\nFrom: b@x.test\r\nSubject: mbox2 {tag}\r\nMessage-ID: <m2{tag}@x>\r\nDate: Mon, 6 Jan 2025 10:00:00 +0700\r\n\r\nPlain\r\n";
        await Mbox.WriteMessageAsync(file, "a@x.test", new DateTimeOffset(2024, 12, 1, 8, 0, 0, TimeSpan.Zero), new MemoryStream(Encoding.UTF8.GetBytes(body1)), default);
        await Mbox.WriteMessageAsync(file, null, new DateTimeOffset(2025, 1, 6, 3, 0, 0, TimeSpan.Zero), new MemoryStream(Encoding.UTF8.GetBytes(body2)), default);
        Encoding.UTF8.GetString(file.ToArray()).Should().Contain("\n>From the start\n").And.Contain("\n>>From quoted\n");

        using var api = await Client("ngoc@bluemail.test");
        var upload = await UploadAsync("ngoc@bluemail.test", $"takeout-{tag}.mbox", file.ToArray());
        var id = (await Data(await api.PostAsJsonAsync("/api/v1/me/imports/file", new { uploadId = upload }))).GetGuid();
        var job = await WaitJob(api, "/api/v1/me/imports", id);
        var errors = await Db(db => db.ImportErrors.IgnoreQueryFilters().Where(e => e.JobId == id).Select(e => e.Code + " " + e.Detail).ToListAsync());
        (job.GetProperty("status").GetString(), job.GetProperty("imported").GetInt32()).Should().Be(("DONE", 2), string.Join("; ", errors));

        var (tenant, _, mailbox) = await Who("ngoc@bluemail.test");
        await using var scope = stack.ApiScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var m1 = await db.Messages.IgnoreQueryFilters().SingleAsync(m => m.MailboxId == mailbox && m.Subject == "mbox1 " + tag);
        (m1.Seen, m1.Flagged).Should().Be((false, true));
        (await db.MessageLabels.IgnoreQueryFilters().Where(ml => ml.MessageId == m1.Id).Join(db.Labels.IgnoreQueryFilters(), ml => ml.LabelId, l => l.Id, (ml, l) => l.SystemKey ?? l.Name)
            .ToListAsync()).Should().Contain([SystemLabels.Inbox, "Khách hàng, VIP"]);
        using var raw = new MemoryStream();
        await store.CopyRawAsync(PgMailStore.SystemAccess(tenant, mailbox), m1.Id, raw);
        Encoding.UTF8.GetString(raw.ToArray()).Should().Contain("\r\nFrom the start\r\n>From quoted\r\n", "mboxrd quoting is undone exactly once");
        var m2 = await db.Messages.IgnoreQueryFilters().SingleAsync(m => m.MailboxId == mailbox && m.Subject == "mbox2 " + tag);
        (m2.Seen, m2.Answered).Should().Be((true, true));
        (await db.MessageLabels.IgnoreQueryFilters().Where(ml => ml.MessageId == m2.Id).Join(db.Labels.IgnoreQueryFilters(), ml => ml.LabelId, l => l.Id, (ml, l) => l.Name)
            .ToListAsync()).Should().Contain($"takeout-{tag}", "a file without labels goes under a label named after it");
    }

    private async Task<Guid> DeliverAsync(string email, string subject, DateTimeOffset received, params string[] labels)
    {
        var (tenant, _, mailbox) = await Who(email);
        await using var scope = stack.ApiScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var raw = Encoding.UTF8.GetBytes($"From: x@doitac.test\r\nTo: {email}\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@t>\r\n\r\nbody\r\n");
        var facts = MessageFacts.Extract(MimeMessage.Parse(raw), new MemoryStream(raw));
        return (await store.DeliverAsync(new DeliveryRequest(tenant, new MemoryStream(raw), facts, [new MailboxDelivery(mailbox, labels)], received)))[0].MessageId;
    }

    private async Task DeleteForeverAsync(string email, Guid id)
    {
        var (tenant, _, mailbox) = await Who(email);
        await using var scope = stack.ApiScope();
        await scope.ServiceProvider.GetRequiredService<PgMailStore>().ApplyAsync(PgMailStore.SystemAccess(tenant, mailbox), [id], new MessageChange { Expunge = true });
    }

    private async Task<(DateTimeOffset? Deleted, DateTimeOffset? Expunged, int Holds)> State(Guid id) =>
        await Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => new ValueTuple<DateTimeOffset?, DateTimeOffset?, int>(m.DeletedAt, m.ExpungedAt, m.LegalHoldCount)).SingleAsync());

    [Fact]
    public async Task Legal_hold_keeps_deleted_mail_for_compliance_until_released()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await Client("lan@bluemail.test");
        using var user = await Client("mei@bluemail.test");
        (await user.GetAsync("/api/v1/admin/compliance/holds")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (_, mei, _) = await Who("mei@bluemail.test");

        var before = await DeliverAsync("mei@bluemail.test", "trước " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        var hold = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/holds", new { name = "Vụ việc " + tag, scope = "USER", userId = mei, reason = "Kiện tụng" })))
            .GetGuid();
        var after = await DeliverAsync("mei@bluemail.test", "sau " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        (await State(before)).Holds.Should().Be(1, "existing mail is recomputed");
        (await State(after)).Holds.Should().Be(1, "new mail is held from the moment it arrives");

        await DeleteForeverAsync("mei@bluemail.test", after);
        var s = await State(after);
        (s.Deleted is not null, s.Expunged is null).Should().Be((true, true), "gone for the user, kept for compliance");
        var found = await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/ediscovery/search", new { query = "subject:" + tag }));
        found.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("hiddenFromUser").GetBoolean() && i.GetProperty("held").GetBoolean());
        (await Db(db => db.AuditLogs.IgnoreQueryFilters().Where(a => a.Action == "compliance.ediscovery.search").Select(a => a.NewValue).ToListAsync()))
            .Should().Contain(v => v!.Contains(tag), "every eDiscovery search is audited");

        // Released: the next housekeeping pass removes it for good.
        (await admin.PostAsync($"/api/v1/admin/compliance/holds/{hold}/release", null)).EnsureSuccessStatusCode();
        (await State(after)).Holds.Should().Be(0);
        await stack.Api.Services.GetRequiredService<ComplianceWorker>().HousekeepingAsync(default);
        (await State(after)).Expunged.Should().NotBeNull();
        (await State(before)).Expunged.Should().BeNull("not deleted by the user");

        // Query holds cover what matched when they were placed.
        var q = await DeliverAsync("mei@bluemail.test", "hợp đồng " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        var qh = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/holds", new { name = "Q " + tag, scope = "QUERY", query = "subject:\"hợp đồng " + tag + "\"" })))
            .GetGuid();
        (await Db(db => db.LegalHolds.IgnoreQueryFilters().Where(h => h.Id == qh).Select(h => h.Matched).SingleAsync())).Should().Be(1);
        (await State(q)).Holds.Should().Be(1);
        (await admin.PostAsJsonAsync("/api/v1/admin/compliance/holds", new { name = "bad", scope = "QUERY", query = "from:(" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retention_keep_minimum_and_delete_after()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await Client("lan@bluemail.test");
        var keep = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention", new { name = "Giữ 30 ngày", action = "KEEP_MIN", days = 30, isEnabled = true })))
            .GetGuid();
        (await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention", new { name = "x", action = "KEEP_MIN", days = 30, query = "from:a", isEnabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        try
        {
            var recent = await DeliverAsync("tuan@bluemail.test", "mới " + tag, DateTimeOffset.UtcNow, SystemLabels.Trash);
            await DeleteForeverAsync("tuan@bluemail.test", recent);
            (await State(recent)).Should().Match<(DateTimeOffset? Deleted, DateTimeOffset? Expunged, int Holds)>(x => x.Deleted != null && x.Expunged == null,
                "younger than the minimum: only hidden");
            var old = await DeliverAsync("tuan@bluemail.test", "cũ " + tag, DateTimeOffset.UtcNow.AddDays(-40), SystemLabels.Trash);
            await DeleteForeverAsync("tuan@bluemail.test", old);
            (await State(old)).Expunged.Should().NotBeNull();
        }
        finally
        {
            (await admin.DeleteAsync($"/api/v1/admin/compliance/retention/{keep}")).EnsureSuccessStatusCode();
        }

        var ancient = await DeliverAsync("tuan@bluemail.test", "lưu trữ " + tag, DateTimeOffset.UtcNow.AddDays(-400), SystemLabels.Inbox);
        var fresh = await DeliverAsync("tuan@bluemail.test", "lưu trữ " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        var rule = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention",
            new { name = "Xoá sau 1 năm", action = "DELETE_AFTER", days = 365, query = "subject:" + tag, isEnabled = true }))).GetGuid();
        await stack.Api.Services.GetRequiredService<ComplianceWorker>().HousekeepingAsync(default);
        (await State(ancient)).Expunged.Should().NotBeNull();
        (await State(fresh)).Expunged.Should().BeNull();
        (await Data(await admin.GetAsync("/api/v1/admin/compliance/retention"))).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == rule).GetProperty("lastRunDeleted").GetInt32().Should().Be(1);
        await admin.DeleteAsync($"/api/v1/admin/compliance/retention/{rule}");
    }

    private static async Task<byte[]> DownloadAsync(HttpClient api, Guid exportId)
    {
        var link = (await Data(await api.PostAsync($"/api/v1/exports/{exportId}/link", null))).GetString()!;
        var anonymous = api.BaseAddress is null ? throw new InvalidOperationException() : new HttpClient { BaseAddress = api.BaseAddress };
        var r = await api.GetAsync(link);
        r.EnsureSuccessStatusCode();
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        anonymous.Dispose();
        return await r.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task EDiscovery_export_has_a_manifest_and_user_export_has_mail_contacts_settings()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await Client("lan@bluemail.test");
        await DeliverAsync("hoa@bluemail.test", "điều tra " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        await DeliverAsync("quang@bluemail.test", "điều tra " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        var id = (await Data(await admin.PostAsJsonAsync("/api/v1/admin/compliance/ediscovery/export", new { query = "subject:" + tag, format = "MBOX" }))).GetGuid();
        var job = await WaitJob(admin, "/api/v1/admin/compliance/exports", id);
        job.GetProperty("status").GetString().Should().Be("DONE", job.ToString());
        var bytes = await DownloadAsync(admin, id);
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(job.GetProperty("sha256").GetString());
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            zip.Entries.Select(e => e.FullName).Should().Contain(["hoa@bluemail.test.mbox", "quang@bluemail.test.mbox", "manifest.csv"]);
            var manifest = await new StreamReader(zip.GetEntry("manifest.csv")!.Open()).ReadToEndAsync();
            await using var mbox = zip.GetEntry("hoa@bluemail.test.mbox")!.Open();
            var entries = new List<MboxEntry>();
            await foreach (var e in Mbox.ReadAsync(mbox, 10_000_000))
            {
                entries.Add(e);
            }

            entries.Should().ContainSingle();
            manifest.Should().Contain(Convert.ToHexStringLower(SHA256.HashData(entries[0].Content)), "the manifest hash is of the exported message bytes");
        }

        (await admin.PostAsync($"/api/v1/exports/{id}/link", null)).IsSuccessStatusCode.Should().BeTrue();
        using var other = await Client("minh@bluemail.test");
        (await other.PostAsync($"/api/v1/exports/{id}/link", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, "links are for the requester only");

        // "Download my data".
        await DeliverAsync("minh@bluemail.test", "của tôi " + tag, DateTimeOffset.UtcNow, SystemLabels.Inbox);
        var mine = (await Data(await other.PostAsync("/api/v1/me/export", null))).GetGuid();
        (await other.PostAsync("/api/v1/me/export", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var done = await WaitJob(other, "/api/v1/me/exports", mine);
        done.GetProperty("status").GetString().Should().Be("DONE");
        using var userZip = new ZipArchive(new MemoryStream(await DownloadAsync(other, mine)));
        userZip.Entries.Select(e => e.FullName).Should().Contain(["mail/INBOX.mbox", "contacts.vcf", "settings.json"]);
        (await new StreamReader(userZip.GetEntry("settings.json")!.Open()).ReadToEndAsync()).Should().Contain("minh@bluemail.test");
        (await stack.Api.CreateClient().GetAsync("/api/v1/exports/d/forged-token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
