using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Protocols.Smtp;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Outbound;

public sealed record OutboundOptions
{
    public required string Helo { get; init; }
    public int SmtpPort { get; init; } = 25;
    public int BatchSize { get; init; } = 100;
    public int MaxParallelDomains { get; init; } = 32;
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(15);
    public bool UseIpv6 { get; init; }

    /// <summary>Relay everything through this host instead of MX (PROMPT 2.4 smarthost), "host:port".</summary>
    public string? Smarthost { get; init; }

    /// <summary>Test hook: time source.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

internal sealed record Claimed(long Id, Guid MessageId, Guid TenantId, string Rcpt, string Domain, int Attempts);

/// <summary>
/// Delivers the outbound queue (PROMPT 2.4). Recipients are claimed with a lease (UPDATE ... locked_until ...
/// FOR UPDATE SKIP LOCKED), so any number of workers can run; a crashed worker's lease expires and the recipient
/// is taken again. The outcome is written right after the remote server answers, per recipient.
/// </summary>
public sealed class DeliveryWorker(
    IServiceScopeFactory scopes,
    MxResolver mx,
    MtaStsResolver sts,
    DnsClient dns,
    OutboundOptions options,
    ILogger<DeliveryWorker> log)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _domainBackoff = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _domainRate = new(StringComparer.OrdinalIgnoreCase);
    private List<DestinationLimit>? _limits;

    /// <summary>One pass over due recipients. Returns how many recipients were attempted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        _limits ??= await db.DestinationLimits.AsNoTracking().ToListAsync(ct);
        var now = options.Clock();
        var batch = await db.Database.SqlQuery<ClaimedRow>($"""
            UPDATE mta.outbound_recipients r SET locked_until = {now + options.Lease}
            WHERE r.id IN (
                SELECT id FROM mta.outbound_recipients
                WHERE status IN ('QUEUED', 'DEFERRED') AND next_attempt_at <= {now} AND (locked_until IS NULL OR locked_until < {now})
                ORDER BY next_attempt_at
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING r.id, r.outbound_message_id AS message_id, r.tenant_id, r.rcpt, r.domain, r.attempts -- snake_case: the naming convention applies to SqlQuery types
            """).ToListAsync(ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        var groups = batch.Select(b => new Claimed(b.Id, b.MessageId, b.TenantId, b.Rcpt, b.Domain, b.Attempts))
            .GroupBy(c => (c.MessageId, c.Domain)).ToList();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelDomains, CancellationToken = ct },
            async (g, token) =>
            {
                try
                {
                    await DeliverGroupAsync(g.Key.MessageId, g.Key.Domain, g.ToList(), token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Unexpected failure (e.g. S3 unreachable): leave the lease to expire and try again later.
                    log.LogError(e, "Delivery of {Message} to {Domain} failed unexpectedly", g.Key.MessageId, g.Key.Domain);
                }
            });
        return batch.Count;
    }

    private sealed record ClaimedRow(long Id, Guid MessageId, Guid TenantId, string Rcpt, string Domain, int Attempts);

    private DestinationLimit LimitFor(string domain)
    {
        var limits = _limits ?? [];
        return limits.FirstOrDefault(l => l.DomainPattern.Equals(domain, StringComparison.OrdinalIgnoreCase))
               ?? limits.FirstOrDefault(l => l.DomainPattern.StartsWith("*.", StringComparison.Ordinal) && domain.EndsWith(l.DomainPattern[1..], StringComparison.OrdinalIgnoreCase))
               ?? limits.FirstOrDefault(l => l.DomainPattern == "*")
               ?? new DestinationLimit { DomainPattern = "*", MaxConnections = 10, MaxMessagesPerMinute = 600 };
    }

    private bool RateAllows(string domain, int perMinute)
    {
        var q = _domainRate.GetOrAdd(domain, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            var now = options.Clock();
            while (q.Count > 0 && q.Peek() < now.AddMinutes(-1))
            {
                q.Dequeue();
            }

            if (q.Count >= perMinute)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }

    private async Task DeliverGroupAsync(Guid messageId, string domain, List<Claimed> rcpts, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var msg = await db.OutboundMessages.AsNoTracking().IgnoreQueryFilters().FirstAsync(m => m.Id == messageId, ct);

        if (_domainBackoff.TryGetValue(domain, out var until) && until > options.Clock())
        {
            await RecordAsync(sp, msg, rcpts.Select(r => (r, Outcome.Deferred("421 4.7.0 local backoff: receiver asked to slow down", null, null, null))).ToList(), ct);
            return;
        }

        var limit = LimitFor(domain);
        if (!RateAllows(domain, Math.Max(1, limit.MaxMessagesPerMinute)))
        {
            // Not an attempt: put back shortly without counting.
            await db.OutboundRecipients.IgnoreQueryFilters().Where(r => rcpts.Select(x => x.Id).Contains(r.Id))
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.NextAttemptAt, options.Clock().AddSeconds(30)), ct);
            return;
        }

        var slot = _domainSlots.GetOrAdd(domain, _ => new SemaphoreSlim(Math.Max(1, limit.MaxConnections)));
        await slot.WaitAsync(ct);
        try
        {
            var raw = new MemoryStream();
            await sp.GetRequiredService<BlobStore>().ReadAsync(msg.TenantId, msg.BlobSha256, raw, ct: ct);
            var results = await AttemptAsync(domain, msg, raw.ToArray(), rcpts.Select(r => r.Rcpt).ToList(), ct);
            await RecordAsync(sp, msg, rcpts.Select(r => (r, results[r.Rcpt])).ToList(), ct);
        }
        finally
        {
            slot.Release();
        }
    }

    internal sealed record Outcome(RecipientDeliveryStatus Status, string Response, string? Mx, string? Ip, bool? Tls, bool Suppress = false)
    {
        public static Outcome Deferred(string response, string? mx, string? ip, bool? tls) => new(RecipientDeliveryStatus.Deferred, response, mx, ip, tls);
        public static Outcome Bounced(string response, string? mx, string? ip, bool? tls, bool suppress = false) => new(RecipientDeliveryStatus.Bounced, response, mx, ip, tls, suppress);
    }

    /// <summary>Tries the MX hosts in order until one gives a definite answer for the recipients.</summary>
    private async Task<Dictionary<string, Outcome>> AttemptAsync(string domain, OutboundMessage msg, byte[] content, List<string> rcpts, CancellationToken ct)
    {
        Dictionary<string, Outcome> All(Outcome o) => rcpts.ToDictionary(r => r, _ => o, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<MxHost> hosts;
        int port = options.SmtpPort;
        StsPolicy? policy = null;
        if (options.Smarthost is { } smart)
        {
            var colon = smart.LastIndexOf(':');
            hosts = [new MxHost(colon > 0 ? smart[..colon] : smart, 0)];
            if (colon > 0)
            {
                port = int.Parse(smart[(colon + 1)..], CultureInfo.InvariantCulture);
            }
        }
        else
        {
            var route = await mx.ResolveAsync(domain, ct);
            switch (route.Error)
            {
                case RouteError.NoSuchDomain:
                    return All(Outcome.Bounced("550 5.1.2 Recipient domain does not exist (" + domain + ")", null, null, null));
                case RouteError.NullMx:
                    return All(Outcome.Bounced("556 5.1.10 Recipient domain accepts no mail (null MX)", null, null, null));
                case RouteError.Temporary:
                    return All(Outcome.Deferred("451 4.4.3 DNS lookup failed for " + domain + ": " + route.Detail, null, null, null));
            }

            hosts = route.Hosts;
            policy = await sts.GetAsync(domain, ct);
            if (policy is { Mode: StsMode.Enforce })
            {
                // RFC 8461 4.1: only MX hosts listed in the policy may be used.
                hosts = hosts.Where(h => policy.Matches(h.Host)).ToList();
                if (hosts.Count == 0)
                {
                    return All(Outcome.Deferred("451 4.7.5 No MX host matches the MTA-STS policy of " + domain, null, null, null));
                }
            }
        }

        Dictionary<string, Outcome>? last = null;
        foreach (var host in hosts)
        {
            List<IPAddress> ips;
            if (IPAddress.TryParse(host.Host, out var literal))
            {
                ips = [literal]; // smarthost given by address: nothing to resolve
            }
            else
            {
                var (status, addresses) = await dns.LookupAddressesAsync(host.Host, ipv6: false, ct);
                ips = status == DnsStatus.Ok ? addresses.ToList() : [];
            }

            if (options.UseIpv6 && !IPAddress.TryParse(host.Host, out _))
            {
                var (s6, a6) = await dns.LookupAddressesAsync(host.Host, ipv6: true, ct);
                if (s6 == DnsStatus.Ok)
                {
                    ips.AddRange(a6);
                }
            }

            foreach (var ip in ips.Take(3))
            {
                var r = await TryHostAsync(host.Host, ip, port, msg, content, rcpts, policy, ct);
                if (r.Definite)
                {
                    return r.Results;
                }

                last = r.Results;
            }
        }

        return last ?? All(Outcome.Deferred("451 4.4.4 No reachable MX host for " + domain, null, null, null));
    }

    private sealed record HostAttempt(bool Definite, Dictionary<string, Outcome> Results);

    private async Task<HostAttempt> TryHostAsync(string host, IPAddress ip, int port, OutboundMessage msg, byte[] content, List<string> rcpts, StsPolicy? policy,
        CancellationToken ct)
    {
        var tlsRequired = policy is { Mode: StsMode.Enforce };
        await using var client = new SmtpClient(new SmtpClientOptions
        {
            Helo = options.Helo,
            Tls = tlsRequired ? TlsRequirement.Required : TlsRequirement.Opportunistic,
            ValidCertificateNames = tlsRequired ? [host] : [],
        });
        var mxName = host;
        var ipText = ip.ToString();
        try
        {
            await client.ConnectAsync(host, ip, port, ct);
            var (perRcpt, final) = await client.SendAsync(msg.EnvelopeFrom, rcpts, content, msg.SmtpUtf8, msg.EightBit, ct);
            await client.QuitAsync(ct);
            var results = new Dictionary<string, Outcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in perRcpt)
            {
                var resp = r.Response;
                Outcome o;
                if (!resp.IsPositive)
                {
                    o = resp.IsPermanent
                        ? Outcome.Bounced(resp.ToString(), mxName, ipText, client.TlsActive, Throttle.IsUnknownUser(resp.Code, resp.Enhanced))
                        : Outcome.Deferred(resp.ToString(), mxName, ipText, client.TlsActive);
                }
                else if (final is null)
                {
                    o = Outcome.Deferred("451 4.3.0 no data phase", mxName, ipText, client.TlsActive);
                }
                else if (final.IsPositive)
                {
                    o = new Outcome(RecipientDeliveryStatus.Delivered, final.ToString(), mxName, ipText, client.TlsActive);
                }
                else
                {
                    o = final.IsPermanent ? Outcome.Bounced(final.ToString(), mxName, ipText, client.TlsActive) : Outcome.Deferred(final.ToString(), mxName, ipText, client.TlsActive);
                }

                results[r.Recipient] = o;
            }

            return new HostAttempt(true, results);
        }
        catch (SmtpClientException e)
        {
            var text = e.Response?.ToString() ?? "451 4.4.2 " + e.Message;
            var outcome = e.Transient || e.Response is { IsPermanent: false }
                ? Outcome.Deferred(text, mxName, ipText, client.TlsActive)
                : Outcome.Bounced(text, mxName, ipText, client.TlsActive);
            if (e.Response is { } resp && Throttle.IsRateLimit(resp.Code, resp.Enhanced, resp.Text))
            {
                var rcptDomain = rcpts[0][(rcpts[0].LastIndexOf('@') + 1)..];
                _domainBackoff[rcptDomain] = options.Clock().AddMinutes(5); // every recipient of this group has the same domain
            }

            // A permanent answer to the whole transaction is definite; connection problems try the next host.
            return new HostAttempt(outcome.Status == RecipientDeliveryStatus.Bounced, rcpts.ToDictionary(r => r, _ => outcome, StringComparer.OrdinalIgnoreCase));
        }
    }

    private async Task RecordAsync(IServiceProvider sp, OutboundMessage msg, List<(Claimed Rcpt, Outcome Outcome)> outcomes, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var now = options.Clock();
        var dsnFailed = new List<DsnRecipient>();
        var dsnDelayed = new List<DsnRecipient>();
        foreach (var (r, o) in outcomes)
        {
            var attempts = r.Attempts + 1;
            var status = o.Status;
            DateTimeOffset? delayNotice = null;
            if (status == RecipientDeliveryStatus.Deferred && now - msg.CreatedAt >= RetrySchedule.GiveUpAfter)
            {
                status = RecipientDeliveryStatus.Expired;
            }

            var next = now + RetrySchedule.After(attempts);
            var row = await db.OutboundRecipients.IgnoreQueryFilters().FirstAsync(x => x.Id == r.Id, ct);
            row.Status = status;
            row.Attempts = attempts;
            row.NextAttemptAt = next;
            row.LockedUntil = null;
            row.LastMx = o.Mx;
            row.LastIp = o.Ip;
            row.LastTls = o.Tls;
            row.LastResponse = o.Response.Length > 1000 ? o.Response[..1000] : o.Response;
            if (status == RecipientDeliveryStatus.Delivered)
            {
                row.DeliveredAt = now;
            }

            var dsn = new DsnRecipient(r.Rcpt, status == RecipientDeliveryStatus.Deferred ? DsnAction.Delayed : DsnAction.Failed, StatusCode(o.Response, status),
                o.Mx, o.Response, now);
            if (status is RecipientDeliveryStatus.Bounced or RecipientDeliveryStatus.Expired && row.DsnSentAt is null && !msg.IsNotification)
            {
                dsnFailed.Add(dsn);
                row.DsnSentAt = now;
            }
            else if (status == RecipientDeliveryStatus.Deferred && row.DelayNoticeAt is null && now - msg.CreatedAt >= RetrySchedule.DelayNoticeAfter && !msg.IsNotification)
            {
                dsnDelayed.Add(dsn);
                row.DelayNoticeAt = now;
                delayNotice = now;
            }

            if (o.Suppress)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO mta.suppressions (tenant_id, address, reason, detail, created_at, expires_at)
                    VALUES ({msg.TenantId}, {r.Rcpt.ToLowerInvariant()}, 'HARD_BOUNCE', {row.LastResponse}, now(), now() + interval '90 days')
                    ON CONFLICT (tenant_id, address) DO UPDATE SET detail = excluded.detail, expires_at = excluded.expires_at
                    """, ct);
            }

            db.MailLog.Add(new MailLogEntry
            {
                TenantId = msg.TenantId,
                Direction = MailDirection.Outbound,
                RfcMessageId = msg.RfcMessageId,
                EnvelopeFrom = msg.EnvelopeFrom,
                Rcpt = r.Rcpt,
                Size = msg.Size,
                Status = status switch
                {
                    RecipientDeliveryStatus.Delivered => MailLogStatus.Delivered,
                    RecipientDeliveryStatus.Deferred => MailLogStatus.Deferred,
                    _ => MailLogStatus.Bounced,
                },
                RemoteIp = o.Ip,
                RemoteHost = o.Mx,
                SmtpResponse = row.LastResponse,
                Details = System.Text.Json.JsonSerializer.Serialize(new { attempt = attempts, tls = o.Tls, delayNotice }),
                OccurredAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        if (dsnFailed.Count > 0)
        {
            await NotifyAsync(sp, msg, dsnFailed, ct);
        }

        if (dsnDelayed.Count > 0)
        {
            await NotifyAsync(sp, msg, dsnDelayed, ct);
        }
    }

    private static string StatusCode(string response, RecipientDeliveryStatus status)
    {
        var parts = response.Split(' ');
        if (parts.Length > 1 && parts[1].Count(c => c == '.') == 2 && char.IsAsciiDigit(parts[1][0]))
        {
            return parts[1];
        }

        return status == RecipientDeliveryStatus.Deferred ? "4.0.0" : status == RecipientDeliveryStatus.Expired ? "4.4.7" : "5.0.0";
    }

    /// <summary>The sender is always one of our users: DSNs go straight into their mailbox.</summary>
    private async Task NotifyAsync(IServiceProvider sp, OutboundMessage msg, List<DsnRecipient> rcpts, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        if (msg.SenderUserId is not { } user)
        {
            return;
        }

        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == msg.TenantId && m.OwnerId == user && m.DeletedAt == null)
            .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (mailbox is null)
        {
            return;
        }

        var raw = new MemoryStream();
        await sp.GetRequiredService<BlobStore>().ReadAsync(msg.TenantId, msg.BlobSha256, raw, ct: ct);
        var original = raw.ToArray();
        var subject = MimeSubject(original);
        var delayed = rcpts.All(r => r.Action == DsnAction.Delayed);
        var id = $"dsn-{(delayed ? "d" : "f")}-{Guid.NewGuid():N}@{options.Helo}";
        var dsn = DsnBuilder.Build(options.Helo, msg.EnvelopeFrom, msg.SenderLocale, subject, msg.RfcMessageId, DsnBuilder.HeadersOf(original), rcpts, msg.CreatedAt, id,
            options.Clock());
        await OutgoingDispatcher.DeliverRawAsync(sp.GetRequiredService<PgMailStore>(), msg.TenantId, dsn,
            new MailboxDelivery(mailbox.Value, [SystemLabels.Inbox, SystemLabels.CategoryUpdates]), ct);
    }

    private static string MimeSubject(byte[] raw)
    {
        try
        {
            var s = new MemoryStream(raw, writable: false);
            return Mime.MimeMessage.Parse(s).Subject ?? "";
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }
}
