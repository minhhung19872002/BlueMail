using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Outbound;
using BlueMail.Protocols.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.IntegrationTests;

/// <summary>A receiving MTA for tests: per-recipient answers are programmable; counts fully acknowledged messages.</summary>
internal sealed class FakeMx : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public ConcurrentDictionary<string, Func<SmtpReply?>> Rcpt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentBag<(string[] Rcpts, byte[] Data)> Received { get; } = [];

    /// <summary>When set, the final reply to DATA/BDAT waits for it (to simulate a worker killed mid-transaction).</summary>
    public TaskCompletionSource? HoldFinalReply { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeMx()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
                c = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (c)
                {
                    var session = new SmtpServerSession(new SmtpServerOptions { Hostname = "mx.partner.test" }, new Handler(this),
                        new SmtpEnvelope { RemoteAddress = IPAddress.Loopback });
                    await new SmtpTransport(c.GetStream(), session, new SmtpTransportOptions(), NullLogger.Instance).RunAsync(_stop.Token);
                }
            });
        }
    }

    private sealed class Handler(FakeMx mx) : ISmtpHandler
    {
        public ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope s, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);
        public ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope s, SmtpPath f, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

        public ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope s, SmtpPath r, CancellationToken ct) =>
            ValueTask.FromResult(mx.Rcpt.TryGetValue(r.Address, out var f) ? f() : null);

        public async ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope s, Stream m, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await m.CopyToAsync(ms, ct);
            if (mx.HoldFinalReply is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            mx.Received.Add((s.Recipients.Select(r => r.Address).ToArray(), ms.ToArray()));
            return new SmtpReply(250, "2.0.0", "queued as fake");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }
}

[Collection(StackCollection.Name)]
public class SendingTests(StackFixture stack)
{
    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> SaveDraft(HttpClient api, object state)
    {
        var d = await Data(await api.PostAsJsonAsync("/api/v1/mail/drafts", new { state }));
        return d.GetProperty("id").GetGuid();
    }

    private async Task<T> Db<T>(Func<BlueMailDbContext, Task<T>> f)
    {
        await using var scope = stack.ApiScope();
        return await f(scope.ServiceProvider.GetRequiredService<BlueMailDbContext>());
    }

    private Task MakeDue(Guid outgoingId) => Db(db => db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == outgoingId)
        .ExecuteUpdateAsync(o => o.SetProperty(x => x.SendAt, DateTimeOffset.UtcNow.AddSeconds(-1))));

    private OutgoingDispatcher Dispatcher => stack.Api.Services.GetRequiredService<OutgoingDispatcher>();

    private Task<int> CountMessages(string email, string rfcId, string label) => Db(db =>
        (from m in db.Messages.IgnoreQueryFilters()
         join b in db.Mailboxes.IgnoreQueryFilters() on m.MailboxId equals b.Id
         join u in db.Users.IgnoreQueryFilters() on b.OwnerId equals u.Id
         join ml in db.MessageLabels.IgnoreQueryFilters() on m.Id equals ml.MessageId
         join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
         where u.PrimaryEmail == email && m.RfcMessageId == rfcId && m.ExpungedAt == null && l.SystemKey == label
         select m.Id).CountAsync());

    [Fact]
    public async Task Send_undo_resend_and_crash_safe_dispatch_never_duplicates()
    {
        using var api = await Client("priya@acme.test");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var draftId = await SaveDraft(api, new
        {
            to = new[] { new { name = (string?)"Tomáš", address = "tomas@acme.test" }, new { name = (string?)null, address = "partner1@partner.test" } },
            bcc = new[] { new { name = (string?)null, address = "amara@acme.test" } },
            subject = "Kế hoạch quý I " + tag,
            html = "<p>Xin chào <b>mọi người</b></p>",
        });

        var first = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { undoSeconds = 30 }));
        var undo = await Data(await api.PostAsync($"/api/v1/mail/outgoing/{first.GetProperty("outgoingId").GetGuid()}/cancel", null));
        undo.GetGuid().Should().Be(draftId, "undo gives the draft back to the compose window");

        var send = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { undoSeconds = 30 }));
        (await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { undoSeconds = 30 })).StatusCode.Should().Be(HttpStatusCode.Conflict, "one pending send per draft");
        var outgoingId = send.GetProperty("outgoingId").GetGuid();

        // Two dispatchers race for the same due send.
        await MakeDue(outgoingId);
        await Task.WhenAll(Dispatcher.RunOnceAsync(default), Dispatcher.RunOnceAsync(default));
        var row = await Db(db => db.Outgoing.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == outgoingId));
        row.Status.Should().Be(OutgoingStatus.Submitted);
        var rfcId = row.RfcMessageId.Trim('<', '>');

        // A crash right after submitting: the row is claimed again after its lease and must not duplicate anything.
        await Db(db => db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == outgoingId).ExecuteUpdateAsync(o => o
            .SetProperty(x => x.Status, OutgoingStatus.Submitting).SetProperty(x => x.ClaimedAt, DateTimeOffset.UtcNow.AddMinutes(-10))));
        await Dispatcher.RunOnceAsync(default);

        (await CountMessages("priya@acme.test", rfcId, SystemLabels.Sent)).Should().Be(1);
        (await CountMessages("tomas@acme.test", rfcId, SystemLabels.Inbox)).Should().Be(1, "internal recipients are delivered directly, once");
        (await CountMessages("amara@acme.test", rfcId, SystemLabels.Inbox)).Should().Be(1, "Bcc recipients get it too");
        var queued = await Db(db => db.OutboundMessages.IgnoreQueryFilters().Where(m => m.OutgoingId == outgoingId).ToListAsync());
        queued.Should().ContainSingle();
        var rcpts = await Db(db => db.OutboundRecipients.IgnoreQueryFilters().Where(r => r.OutboundMessageId == queued[0].Id).ToListAsync());
        rcpts.Select(r => r.Rcpt).Should().Equal("partner1@partner.test");

        // What leaves for the Internet is DKIM-signed and has no Bcc header; the draft is gone.
        await using var scope = stack.ApiScope();
        var raw = new MemoryStream();
        await scope.ServiceProvider.GetRequiredService<BlobStore>().ReadAsync(queued[0].TenantId, queued[0].BlobSha256, raw);
        var text = Encoding.UTF8.GetString(raw.ToArray());
        text.Should().StartWith("DKIM-Signature:").And.NotContain("amara@acme.test").And.Contain("Message-ID: <" + rfcId + ">");
        (await api.GetAsync($"/api/v1/mail/drafts/{draftId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scheduled_send_waits_in_scheduled_and_cancel_returns_it_to_drafts()
    {
        using var api = await Client("joao@acme.test");
        var draftId = await SaveDraft(api, new { to = new[] { new { address = "x@partner.test" } }, subject = "Agendado", html = "<p>Olá</p>" });
        var at = DateTimeOffset.UtcNow.AddHours(3);
        var s = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { scheduleAt = at }));
        s.GetProperty("scheduled").GetBoolean().Should().BeTrue();
        var draft = await Data(await api.GetAsync($"/api/v1/mail/drafts/{draftId}"));
        var messageId = draft.GetProperty("messageId").GetGuid();
        var labels = await Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters() join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                     where ml.MessageId == messageId select l.SystemKey).ToListAsync());
        labels.Should().Contain(SystemLabels.Scheduled).And.NotContain(SystemLabels.Drafts);

        await Dispatcher.RunOnceAsync(default);
        (await Db(db => db.Outgoing.IgnoreQueryFilters().SingleAsync(o => o.DraftId == draftId))).Status.Should().Be(OutgoingStatus.Scheduled, "not due yet");

        await Data(await api.PostAsync($"/api/v1/mail/outgoing/{s.GetProperty("outgoingId").GetGuid()}/cancel", null));
        labels = await Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters() join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                 where ml.MessageId == messageId select l.SystemKey).ToListAsync());
        labels.Should().Contain(SystemLabels.Drafts).And.NotContain(SystemLabels.Scheduled);
    }

    [Fact]
    public async Task Sender_cannot_use_an_address_that_is_not_theirs()
    {
        using var api = await Client("david@acme.test");
        var draftId = await SaveDraft(api, new { from = "olivia@acme.test", to = new[] { new { address = "x@partner.test" } }, subject = "spoof", html = "x" });
        (await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<(Guid MessageId, string RfcId)> QueueExternal(string sender, params string[] rcpts)
    {
        using var api = await Client(sender);
        var draftId = await SaveDraft(api, new { to = rcpts.Select(r => new { address = r }).ToArray(), subject = "Ra ngoài " + Guid.NewGuid().ToString("N")[..6], html = "<p>hi</p>" });
        var s = await Data(await api.PostAsJsonAsync($"/api/v1/mail/drafts/{draftId}/send", new { }));
        var outgoingId = s.GetProperty("outgoingId").GetGuid();
        await MakeDue(outgoingId);
        await Dispatcher.RunOnceAsync(default);
        var m = await Db(db => db.OutboundMessages.IgnoreQueryFilters().SingleAsync(x => x.OutgoingId == outgoingId));
        return (m.Id, m.RfcMessageId!);
    }

    private DeliveryWorker Worker(FakeMx mx, Func<DateTimeOffset> clock)
    {
        var dns = new DnsClient(new DnsClientOptions { Servers = [new IPEndPoint(IPAddress.Loopback, 9)], Timeout = TimeSpan.FromMilliseconds(200), Attempts = 1 });
        return new DeliveryWorker(stack.Api.Services.GetRequiredService<IServiceScopeFactory>(), new MxResolver(dns), new MtaStsResolver(dns, new HttpClient()), dns,
            new OutboundOptions { Helo = "out1.acme.test", Smarthost = "127.0.0.1:" + mx.Port, Clock = clock, Lease = TimeSpan.FromMinutes(15) },
            NullLogger<DeliveryWorker>.Instance);
    }

    private Task<List<OutboundRecipient>> Recipients(Guid messageId) =>
        Db(db => db.OutboundRecipients.IgnoreQueryFilters().AsNoTracking().Where(r => r.OutboundMessageId == messageId).OrderBy(r => r.Rcpt).ToListAsync());

    [Fact]
    public async Task Delivery_per_recipient_outcomes_retry_schedule_suppression_and_dsn()
    {
        await using var mx = new FakeMx();
        var attemptsSlow = 0;
        mx.Rcpt["bad@partner.test"] = () => new SmtpReply(550, "5.1.1", "No such user");
        mx.Rcpt["slow@partner.test"] = () => Interlocked.Increment(ref attemptsSlow) == 1 ? new SmtpReply(451, "4.2.1", "Mailbox busy, try later") : null;
        var (msgId, rfcId) = await QueueExternal("lucas@acme.test", "ok@partner.test", "bad@partner.test", "slow@partner.test");
        var now = DateTimeOffset.UtcNow; // after queueing: the rows are due "now" by the database clock
        var worker = Worker(mx, () => now);
        while (await worker.RunOnceAsync(default) > 0)
        {
        }

        var r = await Recipients(msgId);
        r.Single(x => x.Rcpt == "ok@partner.test").Status.Should().Be(RecipientDeliveryStatus.Delivered);
        var bad = r.Single(x => x.Rcpt == "bad@partner.test");
        bad.Status.Should().Be(RecipientDeliveryStatus.Bounced);
        bad.LastResponse.Should().Contain("5.1.1");
        var slow = r.Single(x => x.Rcpt == "slow@partner.test");
        slow.Status.Should().Be(RecipientDeliveryStatus.Deferred);
        slow.NextAttemptAt.Should().BeCloseTo(now + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

        (await Db(db => db.Suppressions.AnyAsync(s => s.Address == "bad@partner.test"))).Should().BeTrue("hard bounces are suppressed");
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.Subject == "Delivery Status Notification (Failure)" && m.InReplyTo == rfcId)))
            .Should().Be(1, "the sender gets one failure DSN for the bounced recipient");

        // The retry happens on schedule, not before.
        now += TimeSpan.FromMinutes(2);
        await worker.RunOnceAsync(default);
        (await Recipients(msgId)).Single(x => x.Rcpt == "slow@partner.test").Status.Should().Be(RecipientDeliveryStatus.Deferred);
        now += TimeSpan.FromMinutes(4);
        await worker.RunOnceAsync(default);
        (await Recipients(msgId)).Single(x => x.Rcpt == "slow@partner.test").Status.Should().Be(RecipientDeliveryStatus.Delivered);

        mx.Received.SelectMany(x => x.Rcpts).Should().BeEquivalentTo(["ok@partner.test", "slow@partner.test"], "each accepted recipient got it exactly once");
        Encoding.UTF8.GetString(mx.Received.First().Data).Should().StartWith("DKIM-Signature:");
    }

    [Fact]
    public async Task Temporary_failures_end_with_delay_notice_then_expiry_dsn()
    {
        await using var mx = new FakeMx();
        mx.Rcpt["never@partner.test"] = () => new SmtpReply(452, "4.2.2", "Over quota");
        var (msgId, rfcId) = await QueueExternal("sofia@acme.test", "never@partner.test");
        var now = DateTimeOffset.UtcNow;
        var worker = Worker(mx, () => now);
        await worker.RunOnceAsync(default);

        now += RetrySchedule.DelayNoticeAfter + TimeSpan.FromMinutes(1);
        await Db(db => db.OutboundRecipients.IgnoreQueryFilters().Where(r => r.OutboundMessageId == msgId).ExecuteUpdateAsync(r => r.SetProperty(x => x.NextAttemptAt, now.AddSeconds(-1))));
        await worker.RunOnceAsync(default);
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.Subject == "Delivery Status Notification (Delay)" && m.InReplyTo == rfcId))).Should().Be(1);

        now += RetrySchedule.GiveUpAfter;
        await Db(db => db.OutboundRecipients.IgnoreQueryFilters().Where(r => r.OutboundMessageId == msgId).ExecuteUpdateAsync(r => r.SetProperty(x => x.NextAttemptAt, now.AddSeconds(-1))));
        await worker.RunOnceAsync(default);
        (await Recipients(msgId)).Single().Status.Should().Be(RecipientDeliveryStatus.Expired);
        (await Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.Subject == "Delivery Status Notification (Failure)" && m.InReplyTo == rfcId))).Should().Be(1);
    }

    [Fact]
    public async Task Worker_killed_before_the_final_reply_retries_after_the_lease_and_delivers_once()
    {
        await using var mx = new FakeMx();
        var (msgId, _) = await QueueExternal("fatima@acme.test", "kill@partner.test");
        var now = DateTimeOffset.UtcNow;
        var worker = Worker(mx, () => now);

        mx.HoldFinalReply = new TaskCompletionSource();
        using (var kill = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var act = () => worker.RunOnceAsync(kill.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        mx.Received.Should().BeEmpty("the receiver never acknowledged the message");
        var r = (await Recipients(msgId)).Single();
        r.Status.Should().Be(RecipientDeliveryStatus.Queued);
        r.LockedUntil.Should().NotBeNull("the lease of the killed worker is still there");

        // Another worker cannot take it while the lease holds; after it expires it can.
        mx.HoldFinalReply = null;
        var second = Worker(mx, () => now);
        (await second.RunOnceAsync(default)).Should().Be(0);
        now += TimeSpan.FromMinutes(16);
        (await second.RunOnceAsync(default)).Should().Be(1);
        (await Recipients(msgId)).Single().Status.Should().Be(RecipientDeliveryStatus.Delivered);
        mx.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task Reply_reply_all_and_forward_prefill_and_thread_together()
    {
        // ivan writes to jiwoo, cc david; jiwoo replies all; the reply joins the same conversation for ivan.
        using var ivan = await Client("ivan@acme.test");
        var d = await SaveDraft(ivan, new
        {
            to = new[] { new { address = "jiwoo@acme.test" } }, cc = new[] { new { address = "david@acme.test" } },
            subject = "Kế hoạch tuần", html = "<p>Gửi kế hoạch</p>",
            attachments = Array.Empty<object>(),
        });
        var s = await Data(await ivan.PostAsJsonAsync($"/api/v1/mail/drafts/{d}/send", new { }));
        await MakeDue(s.GetProperty("outgoingId").GetGuid());
        await Dispatcher.RunOnceAsync(default);

        using var jiwoo = await Client("jiwoo@acme.test");
        var inbox = await Data(await jiwoo.GetAsync("/api/v1/mail/conversations?view=all&limit=100"));
        var conv = inbox.GetProperty("items").EnumerateArray().First(c => c.GetProperty("subject").GetString() == "Kế hoạch tuần");
        var original = conv.GetProperty("latestMessageId").GetGuid();

        var all = await Data(await jiwoo.GetAsync($"/api/v1/mail/compose/from-message/{original}?mode=replyAll"));
        all.GetProperty("subject").GetString().Should().Be("Re: Kế hoạch tuần");
        all.GetProperty("to").EnumerateArray().Select(a => a.GetProperty("address").GetString()).Should().Equal("ivan@acme.test");
        all.GetProperty("cc").EnumerateArray().Select(a => a.GetProperty("address").GetString()).Should().Equal("david@acme.test");
        all.GetProperty("inReplyTo").GetString().Should().NotBeNullOrEmpty();
        all.GetProperty("html").GetString().Should().Contain("<blockquote").And.Contain("Gửi kế hoạch");

        var fwd = await Data(await jiwoo.GetAsync($"/api/v1/mail/compose/from-message/{original}?mode=forward"));
        fwd.GetProperty("subject").GetString().Should().Be("Fwd: Kế hoạch tuần");
        fwd.GetProperty("to").GetArrayLength().Should().Be(0);

        // Send the reply all and check that ivan sees one conversation with both messages.
        var replyDraft = await Data(await jiwoo.PostAsJsonAsync("/api/v1/mail/drafts", new { state = all }));
        var rs = await Data(await jiwoo.PostAsJsonAsync($"/api/v1/mail/drafts/{replyDraft.GetProperty("id").GetGuid()}/send", new { }));
        await MakeDue(rs.GetProperty("outgoingId").GetGuid());
        await Dispatcher.RunOnceAsync(default);
        var ivanList = await Data(await ivan.GetAsync("/api/v1/mail/conversations?view=all&limit=100"));
        var thread = ivanList.GetProperty("items").EnumerateArray().Where(c => c.GetProperty("subject").GetString()!.EndsWith("Kế hoạch tuần", StringComparison.Ordinal)).ToList();
        thread.Should().ContainSingle("the reply joins the conversation (References/In-Reply-To)");
        thread[0].GetProperty("messageCount").GetInt32().Should().Be(2, "ivan's sent copy and jiwoo's reply");
    }
}
