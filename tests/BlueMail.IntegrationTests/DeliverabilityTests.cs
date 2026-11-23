using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Outbound;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 12: VERP return paths and late bounces, complaints, the abuse self-lock, confidential mode, warm-up caps.</summary>
[Collection(StackCollection.Name)]
public partial class DeliverabilityTests(StackFixture stack)
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

    private OutgoingDispatcher Dispatcher => stack.Api.Services.GetRequiredService<OutgoingDispatcher>();

    /// <summary>Saves and sends a draft, then runs the dispatcher; returns the outgoing row.</summary>
    private async Task<Outgoing> SendAsync(HttpClient api, object state)
    {
        var d = await Data(await api.PostAsJsonAsync("/api/v1/mail/drafts", new { state }));
        var draftId = d.GetProperty("id").GetGuid();
        var s = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { undoSeconds = 0 }));
        var outgoingId = s.GetProperty("outgoingId").GetGuid();
        await Db(db => db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == outgoingId).ExecuteUpdateAsync(o => o.SetProperty(x => x.SendAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await Dispatcher.RunOnceAsync(default);
        return await Db(db => db.Outgoing.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == outgoingId));
    }

    private DeliveryWorker Worker(FakeMx mx)
    {
        var dns = new DnsClient(new DnsClientOptions { Servers = [new IPEndPoint(IPAddress.Loopback, 9)], Timeout = TimeSpan.FromMilliseconds(200), Attempts = 1 });
        var scopes = stack.Api.Services.GetRequiredService<IServiceScopeFactory>();
        var options = new OutboundOptions { Helo = "out1.acme.test", Smarthost = "127.0.0.1:" + mx.Port };
        return new DeliveryWorker(scopes, new MxResolver(dns), new MtaStsResolver(dns, new HttpClient()), dns, options,
            new SendingIps(scopes, dns, options, NullLogger<SendingIps>.Instance), stack.Api.Services.GetRequiredService<Verp>(), new DnssecValidator(dns),
            NullLogger<DeliveryWorker>.Instance);
    }

    private static byte[] Crlf(string s) => Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

    [Fact]
    public async Task Late_bounce_to_the_verp_address_marks_the_recipient_suppresses_and_reaches_the_sender()
    {
        using var api = await Client("fatima@acme.test");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var o = await SendAsync(api, new { to = new[] { new { address = $"gone-{tag}@partner.test" }, new { address = $"fine-{tag}@partner.test" } }, subject = "Late " + tag, html = "<p>x</p>" });
        await using var mx = new FakeMx();
        (await Worker(mx).RunOnceAsync(default)).Should().BeGreaterThan(0);

        var queued = await Db(db => db.OutboundMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.OutgoingId == o.Id));
        var verp = stack.Api.Services.GetRequiredService<Verp>();
        mx.MailFrom.Should().Contain(verp.Encode(queued.Id, "acme.test"), "mail to other servers leaves with a VERP return path");

        // Days later the receiving system bounces one recipient to that return path.
        var dsn = Crlf($"""
            From: MAILER-DAEMON@mx.partner.test
            To: {verp.Encode(queued.Id, "acme.test")}
            Subject: Undelivered Mail Returned to Sender
            Message-ID: <late-{tag}@mx.partner.test>
            MIME-Version: 1.0
            Content-Type: multipart/report; report-type=delivery-status; boundary="b"

            --b
            Content-Type: text/plain

            Mailbox removed.
            --b
            Content-Type: message/delivery-status

            Reporting-MTA: dns; mx.partner.test

            Final-Recipient: rfc822; gone-{tag}@partner.test
            Action: failed
            Status: 5.1.1
            Diagnostic-Code: smtp; 550 5.1.1 mailbox removed
            --b--
            """);
        await using (var scope = stack.ApiScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<FeedbackProcessor>();
            (await processor.HandleAsync(dsn, verp.Decode(verp.Encode(queued.Id, "acme.test")), default)).Should().Be(1);
            (await processor.HandleAsync(dsn, queued.Id, default)).Should().Be(0, "the same bounce twice is recorded once");
        }

        var rcpts = await Db(db => db.OutboundRecipients.IgnoreQueryFilters().AsNoTracking().Where(r => r.OutboundMessageId == queued.Id).ToListAsync());
        rcpts.Single(r => r.Rcpt.StartsWith("gone-", StringComparison.Ordinal)).Status.Should().Be(RecipientDeliveryStatus.Bounced);
        rcpts.Single(r => r.Rcpt.StartsWith("fine-", StringComparison.Ordinal)).Status.Should().Be(RecipientDeliveryStatus.Delivered);
        (await Db(db => db.Suppressions.IgnoreQueryFilters().AnyAsync(s => s.Address == $"gone-{tag}@partner.test" && s.Reason == SuppressionReason.HardBounce)))
            .Should().BeTrue();
        (await Db(db => db.Messages.IgnoreQueryFilters().AnyAsync(m => m.RfcMessageId == $"late-{tag}@mx.partner.test" && m.TenantId == queued.TenantId)))
            .Should().BeTrue("the sender sees the bounce in the Inbox");
    }

    [Fact]
    public async Task Complaints_and_bounces_above_the_threshold_pause_sending_until_an_admin_allows_it()
    {
        using var api = await Client("sofia@acme.test");
        var user = await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "sofia@acme.test").Select(u => new { u.Id, u.TenantId }).SingleAsync());

        // 25 external recipients in the last day, 4 of them hard-bounced (16 % > 10 %).
        await Db(async db =>
        {
            var msgId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO mta.outbound_messages (id, tenant_id, sender_user_id, blob_sha256, size, envelope_from, rfc_message_id, smtp_utf8, eight_bit, priority, is_notification, created_at)
                VALUES ({msgId}, {user.TenantId}, {user.Id}, {new byte[32]}, 10, 'sofia@acme.test', {"bulk-" + msgId}, false, false, 0, false, now())
                """);
            for (var i = 0; i < 25; i++)
            {
                var bounced = i < 4;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO mta.outbound_recipients (tenant_id, outbound_message_id, rcpt, domain, status, attempts, next_attempt_at, last_response)
                    VALUES ({user.TenantId}, {msgId}, {"r" + i + "@partner.test"}, 'partner.test', {(bounced ? "BOUNCED" : "DELIVERED")}, 1, now(),
                            {(bounced ? "550 5.1.1 no such user" : "250 ok")})
                    """);
            }

            return 0;
        });
        await using (var scope = stack.ApiScope())
        {
            (await scope.ServiceProvider.GetRequiredService<SenderReputation>().EvaluateAsync(user.TenantId, user.Id, default)).Should().BeTrue();
        }

        var o = await SendAsync(api, new { to = new[] { new { address = "someone@partner.test" } }, subject = "after lock", html = "<p>x</p>" });
        o.Status.Should().Be(OutgoingStatus.Failed);
        o.Error.Should().Be("sending_locked");

        using var admin = await Client("olivia@acme.test");
        var locks = await Data(await admin.GetAsync("/api/v1/admin/sending-locks"));
        var mine = locks.EnumerateArray().Single(l => l.GetProperty("userId").GetGuid() == user.Id);
        mine.GetProperty("reason").GetString().Should().StartWith("hard_bounce_rate");
        mine.GetProperty("hardBounces").GetInt32().Should().Be(4);
        await Data(await admin.DeleteAsync($"/api/v1/admin/sending-locks/{user.Id}"));
        (await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == user.Id).Select(u => u.SendingLockedAt).SingleAsync())).Should().BeNull();

        // The rows that caused the lock are old news once the admin has looked: move them out of the window for other tests.
        await Db(db => db.OutboundMessages.IgnoreQueryFilters().Where(m => m.SenderUserId == user.Id && m.RfcMessageId!.StartsWith("bulk-"))
            .ExecuteUpdateAsync(m => m.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-3))));
    }

    [GeneratedRegex(@"/c/([A-Za-z0-9_-]{32})")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\b(\d{6})\b")]
    private static partial Regex CodePattern();

    private async Task<string> QueuedText(string rcpt, Func<string, bool> match)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var rows = await (from r in db.OutboundRecipients.IgnoreQueryFilters()
                          join m in db.OutboundMessages.IgnoreQueryFilters() on r.OutboundMessageId equals m.Id
                          where r.Rcpt == rcpt
                          orderby m.CreatedAt descending
                          select m).ToListAsync();
        foreach (var m in rows)
        {
            var raw = new MemoryStream();
            await scope.ServiceProvider.GetRequiredService<BlobStore>().ReadAsync(m.TenantId, m.BlobSha256, raw);
            var text = Encoding.UTF8.GetString(raw.ToArray());
            if (match(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException("no queued message for " + rcpt);
    }

    [Fact]
    public async Task Confidential_mode_sends_a_link_with_a_code_and_the_sender_can_withdraw_it()
    {
        using var api = await Client("ivan@acme.test");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var reader = $"reader-{tag}@partner.test";
        var o = await SendAsync(api, new
        {
            to = new[] { new { address = reader }, new { address = "jonas@acme.test" } },
            subject = "Hợp đồng mật " + tag,
            html = "<p>Nội dung <b>bí mật</b> " + tag + "</p>",
            confidential = new { expiresDays = 7, requireCode = true },
        });
        o.Status.Should().Be(OutgoingStatus.Submitted);
        var rfcId = o.RfcMessageId.Trim('<', '>');

        // Outside: only a link, never the content.
        var notice = await QueuedText(reader, t => t.Contains("/c/", StringComparison.Ordinal));
        notice.Should().NotContain("bí mật").And.NotContain(tag + "</p>").And.StartWith("DKIM-Signature:");
        var token = LinkPattern().Match(notice.Replace("=\r\n", "", StringComparison.Ordinal)).Groups[1].Value;
        token.Should().HaveLength(32);

        // A retried dispatch gives the same link and queues nothing new.
        var before = await Db(db => db.OutboundRecipients.IgnoreQueryFilters().CountAsync(r => r.Rcpt == reader));
        await using (var scope = stack.ApiScope())
        {
            var conf = scope.ServiceProvider.GetRequiredService<ConfidentialService>();
            var row = await scope.ServiceProvider.GetRequiredService<BlueMailDbContext>().Outgoing.IgnoreQueryFilters().SingleAsync(x => x.Id == o.Id);
            await conf.SendLinksAsync(row, new DraftConfidential(7, true), "x", [1], [reader], null, null, default);
            conf.TokenFor((await scope.ServiceProvider.GetRequiredService<BlueMailDbContext>().ConfidentialMessages.IgnoreQueryFilters().SingleAsync(m => m.RfcMessageId == rfcId)).Id, reader)
                .Should().Be(token);
        }

        (await Db(db => db.OutboundRecipients.IgnoreQueryFilters().CountAsync(r => r.Rcpt == reader))).Should().Be(before);

        // Inside the organisation the message arrives as written.
        (await Db(db => db.Messages.IgnoreQueryFilters().AnyAsync(m => m.RfcMessageId == rfcId && m.Subject!.Contains(tag)))).Should().BeTrue();

        // The reader's page: without an account, a code is needed.
        var anon = stack.Api.CreateClient();
        var meta = await Data(await anon.GetAsync($"/api/v1/confidential/{token}"));
        meta.GetProperty("requireCode").GetBoolean().Should().BeTrue();
        meta.GetProperty("recipient").GetString().Should().Be("re•••@partner.test");
        (await anon.PostAsJsonAsync($"/api/v1/confidential/{token}/open", new { code = "000000" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await Data(await anon.PostAsync($"/api/v1/confidential/{token}/code", null));
        (await anon.PostAsync($"/api/v1/confidential/{token}/code", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "one code a minute");
        var codeMail = await QueuedText(reader, t => t.Contains("10 minutes", StringComparison.Ordinal) || t.Contains("10 ph", StringComparison.Ordinal));
        var code = CodePattern().Match(codeMail[codeMail.IndexOf("\r\n\r\n", StringComparison.Ordinal)..]).Groups[1].Value;

        var wrong = code == "123456" ? "654321" : "123456";
        (await anon.PostAsJsonAsync($"/api/v1/confidential/{token}/open", new { code = wrong })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var content = await Data(await anon.PostAsJsonAsync($"/api/v1/confidential/{token}/open", new { code }));
        content.GetProperty("htmlDocument").GetString().Should().Contain("bí mật").And.Contain("Content-Security-Policy");

        // The sender sees who opened it and withdraws access.
        var status = await Data(await api.GetAsync($"/api/v1/mail/confidential?rfcMessageId={Uri.EscapeDataString(rfcId)}"));
        status.GetProperty("recipients").EnumerateArray().Single().GetProperty("opens").GetInt32().Should().Be(1);
        var id = status.GetProperty("id").GetGuid();
        await Data(await api.PutAsJsonAsync($"/api/v1/mail/confidential/{id}/revoked", new { revoked = true }));
        (await anon.GetAsync($"/api/v1/confidential/{token}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Somebody else cannot touch it.
        using var other = await Client("lucas@acme.test");
        (await other.PutAsJsonAsync($"/api/v1/mail/confidential/{id}/revoked", new { revoked = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Warm_up_cap_holds_back_delivery_from_a_new_address()
    {
        // A pool for one tenant only, with a single address in warm-up that already used its cap today.
        var tenant = await Db(db => db.Tenants.IgnoreQueryFilters().Where(t => t.Code == "acme").Select(t => t.Id).SingleAsync());
        var pool = Guid.NewGuid();
        await Db(async db =>
        {
            db.IpPools.Add(new IpPool { Id = pool, Name = "warmup-" + pool.ToString("N")[..6], Kind = IpPoolKind.Dedicated, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var ip = new SendingIp { PoolId = pool, Address = "127.0.0.1", Helo = "localhost.test", WarmupStartedAt = DateTimeOffset.UtcNow, PtrOk = true, PtrCheckedAt = DateTimeOffset.UtcNow };
            db.SendingIps.Add(ip);
            await db.SaveChangesAsync();
            db.SendingIpDays.Add(new SendingIpDay { SendingIpId = ip.Id, Day = DateOnly.FromDateTime(DateTime.UtcNow), Sent = 50 });
            await db.SaveChangesAsync();
            return await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenant).ExecuteUpdateAsync(t => t.SetProperty(x => x.IpPoolId, pool));
        });
        try
        {
            using var api = await Client("jonas@acme.test");
            var capped = $"cap-{Guid.NewGuid():N}@partner.test";
            var o = await SendAsync(api, new { to = new[] { new { address = capped } }, subject = "cap", html = "<p>x</p>" });
            await using var mx = new FakeMx();
            await Worker(mx).RunOnceAsync(default);
            mx.Received.Should().NotContain(m => m.Rcpts.Contains(capped), "the only address of the pool reached its warm-up cap for today");
            var r = await Db(db => (from x in db.OutboundRecipients.IgnoreQueryFilters()
                                    join m in db.OutboundMessages.IgnoreQueryFilters() on x.OutboundMessageId equals m.Id
                                    where m.OutgoingId == o.Id select x).AsNoTracking().SingleAsync());
            r.Status.Should().Be(RecipientDeliveryStatus.Queued);
            r.Attempts.Should().Be(0, "held back is not an attempt");
            r.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(20));
            await Db(db => db.OutboundRecipients.IgnoreQueryFilters().Where(x => x.Id == r.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, RecipientDeliveryStatus.Cancelled)));
        }
        finally
        {
            await Db(db => db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenant).ExecuteUpdateAsync(t => t.SetProperty(x => x.IpPoolId, (Guid?)null)));
            await Db(async db =>
            {
                var ips = await db.SendingIps.Where(i => i.PoolId == pool).Select(i => i.Id).ToListAsync();
                await db.SendingIpDays.Where(d => ips.Contains(d.SendingIpId)).ExecuteDeleteAsync();
                await db.SendingIps.Where(i => i.PoolId == pool).ExecuteDeleteAsync();
                return await db.IpPools.Where(p => p.Id == pool).ExecuteDeleteAsync();
            });
        }
    }
}
