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

    /// <summary>DANE for SMTP (RFC 7672): validate DNSSEC and use TLSA records of MX hosts. Env BM_DANE (default on).</summary>
    public bool Dane { get; init; } = true;

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
    SendingIps sources,
    Verp verp,
    DnssecValidator dnssec,
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

        var source = await sources.ChooseAsync(msg.TenantId, ct);
        if (source is null)
        {
            // Every address of the pool is at its warm-up cap (or unusable): not an attempt, try again later.
            await db.OutboundRecipients.IgnoreQueryFilters().Where(r => rcpts.Select(x => x.Id).Contains(r.Id))
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.NextAttemptAt, options.Clock().AddMinutes(30)), ct);
            return;
        }

        var slot = _domainSlots.GetOrAdd(domain, _ => new SemaphoreSlim(Math.Max(1, limit.MaxConnections)));
        await slot.WaitAsync(ct);
        try
        {
            var raw = new MemoryStream();
            await sp.GetRequiredService<BlobStore>().ReadAsync(msg.TenantId, msg.BlobSha256, raw, ct: ct);
            var results = await AttemptAsync(domain, msg, raw.ToArray(), rcpts.Select(r => r.Rcpt).ToList(), source, ct);
            await RecordAsync(sp, msg, rcpts.Select(r => (r, results[r.Rcpt])).ToList(), ct);
            var handedOver = results.Values.Count(o => o.Status == RecipientDeliveryStatus.Delivered);
            if (source.Id is { } ipId && handedOver > 0)
            {
                await sources.CountAsync(ipId, handedOver, ct);
            }

            if (msg.SenderUserId is { } sender && results.Values.Any(o => o.Suppress))
            {
                await sp.GetRequiredService<SenderReputation>().EvaluateAsync(msg.TenantId, sender, ct);
            }
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
    private async Task<Dictionary<string, Outcome>> AttemptAsync(string domain, OutboundMessage msg, byte[] content, List<string> rcpts, SourceIp source,
        CancellationToken ct)
    {
        Dictionary<string, Outcome> All(Outcome o) => rcpts.ToDictionary(r => r, _ => o, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<MxHost> hosts;
        int port = options.SmtpPort;
        StsPolicy? policy = null;
        var mxSecure = false;
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
            if (options.Dane)
            {
                var signedMx = await dnssec.ResolveAsync(domain, DnsType.MX, ct);
                if (signedMx.Status == DnssecStatus.Bogus)
                {
                    // RFC 7672 2.2.1: a bogus MX answer is a lookup failure - wait, never fall back to an unauthenticated answer.
                    await TlsResultAsync(domain, "tlsa", "", "", source, TlsFailures.DnssecInvalid, ct);
                    return All(Outcome.Deferred("451 4.7.5 DNSSEC validation failed for the MX of " + domain + ": " + signedMx.Reason, null, null, null));
                }

                mxSecure = signedMx.Status == DnssecStatus.Secure;
            }

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

            DanePolicy? dane = null;
            if (options.Dane && options.Smarthost is null && ips.Count > 0)
            {
                dane = await Dns.Dane.PolicyAsync(dnssec, host.Host, port, mxSecure, ct);
                if (dane.State == DaneState.Unavailable)
                {
                    // RFC 7672 2.2.1: TLSA lookup failed - this host cannot be used now, the next one may be.
                    await TlsResultAsync(domain, "tlsa", host.Host, "", source, TlsFailures.DnssecInvalid, ct);
                    last = All(Outcome.Deferred("451 4.7.5 TLSA lookup failed for " + host.Host + ": " + dane.Reason, host.Host, null, null));
                    continue;
                }
            }

            foreach (var ip in ips.Take(3))
            {
                var r = await TryHostAsync(domain, host.Host, ip, port, msg, content, rcpts, policy, dane, source, ct);
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

    private async Task<HostAttempt> TryHostAsync(string domain, string host, IPAddress ip, int port, OutboundMessage msg, byte[] content, List<string> rcpts,
        StsPolicy? policy, DanePolicy? dane, SourceIp source, CancellationToken ct)
    {
        // DANE takes precedence over MTA-STS (RFC 8461 2).
        var useDane = dane is { State: DaneState.Required };
        var stsRequired = !useDane && policy is { Mode: StsMode.Enforce };
        var policyType = useDane ? "tlsa" : policy is not null ? "sts" : "no-policy-found";
        await using var client = new SmtpClient(new SmtpClientOptions
        {
            Helo = source.Helo,
            LocalAddress = source.Address,
            Tls = useDane || stsRequired ? TlsRequirement.Required : TlsRequirement.Opportunistic,
            ValidCertificateNames = stsRequired ? [host] : [],
            CertificateCheck = useDane ? (leaf, chain) => Dns.Dane.Matches(dane!, host, leaf, chain) : null,
        });
        var mxName = host;
        var ipText = ip.ToString();
        var greeted = false;
        try
        {
            await client.ConnectAsync(host, ip, port, ct);
            greeted = true;
            await TlsResultAsync(domain, policyType, host, ipText, source, client.TlsActive ? null : TlsFailures.StartTlsNotSupported, ct);
            var (perRcpt, final) = await client.SendAsync(ReturnPath(msg), rcpts, content, msg.SmtpUtf8, msg.EightBit, ct);
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
            if (!greeted && e.Response is null && e.Message.Contains("TLS", StringComparison.Ordinal))
            {
                // The connection stopped at TLS: what the policy required was not met (RFC 8460 4.3).
                var failure = e.Message.Contains("STARTTLS", StringComparison.Ordinal) ? TlsFailures.StartTlsNotSupported
                    : useDane ? TlsFailures.TlsaInvalid
                    : TlsFailures.ValidationFailure;
                await TlsResultAsync(domain, policyType, host, ipText, source, failure, ct);
            }

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

    /// <summary>MAIL FROM: a VERP address for our users' mail (late bounces find the message); SRS and null senders unchanged.</summary>
    private string ReturnPath(OutboundMessage msg)
    {
        if (msg.IsNotification || string.IsNullOrEmpty(msg.EnvelopeFrom) || Application.Features.Rules.Srs.IsSrs(msg.EnvelopeFrom) || msg.SenderUserId is null)
        {
            return msg.EnvelopeFrom;
        }

        return verp.Encode(msg.Id, msg.EnvelopeFrom[(msg.EnvelopeFrom.LastIndexOf('@') + 1)..]);
    }

    /// <summary>One TLS outcome for SMTP TLS reporting (RFC 8460), counted per day, domain, policy, MX and addresses.</summary>
    private async Task TlsResultAsync(string domain, string policyType, string mx, string receivingIp, SourceIp source, string? failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var day = DateOnly.FromDateTime(options.Clock().UtcDateTime);
        var sending = source.Address?.ToString() ?? "";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.tls_results (day, policy_domain, policy_type, mx_host, receiving_ip, sending_ip, failure_type, count)
            VALUES ({day}, {domain.ToLowerInvariant()}, {policyType}, {mx.ToLowerInvariant()}, {receivingIp}, {sending}, {failure}, 1)
            ON CONFLICT (day, policy_domain, policy_type, mx_host, receiving_ip, sending_ip, failure_type) DO UPDATE SET count = mta.tls_results.count + 1
            """, ct);
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

    /// <summary>
    /// DSNs for our users' own mail go straight into their mailbox. A forwarded message (SRS return path) bounces to
    /// the original sender, as SRS intends (PROMPT 6.8), from the null sender.
    /// </summary>
    private async Task NotifyAsync(IServiceProvider sp, OutboundMessage msg, List<DsnRecipient> rcpts, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        if (Application.Features.Rules.Srs.IsSrs(msg.EnvelopeFrom))
        {
            var originalSender = sp.GetRequiredService<Application.Features.Rules.Srs>().Reverse(msg.EnvelopeFrom);
            if (originalSender is null)
            {
                return;
            }

            var raw0 = new MemoryStream();
            await sp.GetRequiredService<BlobStore>().ReadAsync(msg.TenantId, msg.BlobSha256, raw0, ct: ct);
            var forwarded = raw0.ToArray();
            var dsnId = $"dsn-{Guid.NewGuid():N}@{options.Helo}";
            var bounce = DsnBuilder.Build(options.Helo, originalSender, null, MimeSubject(forwarded), msg.RfcMessageId, DsnBuilder.HeadersOf(forwarded), rcpts, msg.CreatedAt,
                dsnId, options.Clock());
            await sp.GetRequiredService<Application.Features.Rules.SystemMail>().SendAsync(msg.TenantId, null, "", originalSender, bounce, notification: true, null, dsnId, ct);
            return;
        }

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
