using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BlueMail.Antispam;
using BlueMail.Application.Features.Rules;
using BlueMail.Auth;
using BlueMail.Dkim;
using BlueMail.Dns;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Smtp;
using BlueMail.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueMail.Mx;

public sealed record MxOptions
{
    public required string Hostname { get; init; }

    /// <summary>authserv-id of Authentication-Results; headers with this id coming from outside are removed (RFC 8601 5).</summary>
    public string AuthServId => Hostname;

    public long MaxMessageBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxRecipients { get; init; } = 100;
}

/// <summary>
/// Inbound business rules (PROMPT 2.1): recipients checked at RCPT, SPF at MAIL FROM, and at end of DATA:
/// DKIM, ARC, DMARC, scanner; DMARC p=reject and malware are refused in-session (never accept-then-bounce);
/// the message is durably stored for every recipient before 250 is sent; every recipient is logged in admin.mail_log.
/// One instance per connection.
/// </summary>
public sealed class MxHandler(
    IServiceScopeFactory scopes,
    DnsClient dns,
    IFileScanner scanner,
    MxOptions options,
    ILogger<MxHandler> log) : ISmtpHandler
{
    private readonly Dictionary<string, ResolvedRecipient> _recipients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounces to our SRS addresses (rcpt -> original sender, tenant), relayed back (PROMPT 6.8).</summary>
    private readonly Dictionary<string, (string Original, Guid Tenant)> _srsBounces = new(StringComparer.OrdinalIgnoreCase);
    private string? _ptr;
    private SpfOutcome? _spf;

    public string? RemoteHost => _ptr;

    public async ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope session, CancellationToken ct)
    {
        try
        {
            // Forward-confirmed reverse DNS (FCrDNS): the PTR name must resolve back to the address.
            var (status, names) = await dns.LookupPtrAsync(session.RemoteAddress, ct);
            if (status == DnsStatus.Ok && names.Count > 0)
            {
                var name = names[0].TrimEnd('.');
                var (s2, addrs) = await dns.LookupAddressesAsync(name, session.RemoteAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6, ct);
                _ptr = s2 == DnsStatus.Ok && addrs.Contains(session.RemoteAddress) ? name : null;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            log.LogDebug(ex, "PTR lookup failed for {Ip}", session.RemoteAddress);
        }

        return null;
    }

    public async ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope session, SmtpPath from, CancellationToken ct)
    {
        _recipients.Clear();
        _srsBounces.Clear();
        var spf = new SpfEvaluator(new DnsClientSpfDns(dns));
        _spf = await spf.CheckAsync(session.RemoteAddress, from.Address, session.Helo, ct);
        if (_spf.Result == SpfResult.Fail)
        {
            // SPF -all alone is not a rejection reason: DMARC decides at end of data (a DKIM pass can still align).
            log.LogInformation("SPF fail for {From} from {Ip}", from.Address, session.RemoteAddress);
        }

        return null;
    }

    public async ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope session, SmtpPath recipient, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RecipientResolver>();
        var r = await resolver.ResolveAsync(recipient.Address, session.DeclaredSize ?? 0, ct);
        if (r.Status == RecipientStatus.Unknown && Application.Features.Rules.Srs.IsSrs(recipient.Address))
        {
            // A bounce for mail we forwarded: valid hash and age, and only from the null sender (no relaying through SRS).
            var original = scope.ServiceProvider.GetRequiredService<Application.Features.Rules.Srs>().Reverse(recipient.Address);
            if (original is not null && session.From is { IsNull: true })
            {
                _srsBounces[recipient.Address] = (original, r.TenantId);
                return null;
            }

            var refused = new SmtpReply(550, "5.1.1", "No such user here");
            await LogAsync(scope, session, r.TenantId, recipient.Address, null, 0, MailLogStatus.Rejected, refused, null, new { srs = original is null ? "invalid" : "not a bounce" }, ct);
            return refused;
        }

        SmtpReply? reply = r.Status switch
        {
            RecipientStatus.Ok => null,
            RecipientStatus.NotOurDomain => new SmtpReply(550, "5.7.1", "Relaying denied"),
            RecipientStatus.Unknown => new SmtpReply(550, "5.1.1", "No such user here"),
            RecipientStatus.TenantSuspended => new SmtpReply(550, "5.2.1", "Mailbox disabled"),
            RecipientStatus.OverQuota => new SmtpReply(452, "4.2.2", "Mailbox full"),
            _ => SmtpReply.TemporaryFailure,
        };
        if (reply is null && r.Status == RecipientStatus.Ok)
        {
            // Greylisting (optional per tenant, off by default): an unknown (network, sender, recipient) retries later.
            var settings = await scope.ServiceProvider.GetRequiredService<MxSpamFilter>().SettingsAsync(r.TenantId, ct);
            if (settings.Greylisting && !await scope.ServiceProvider.GetRequiredService<Application.Features.Antispam.Greylisting>()
                    .PassAsync(r.TenantId, session.RemoteAddress, session.From?.Address ?? "", recipient.Address, ct))
            {
                reply = new SmtpReply(451, "4.7.1", "Greylisted, please try again in a few minutes");
            }
        }

        if (reply is null)
        {
            _recipients[recipient.Address] = r;
        }
        else
        {
            await LogAsync(scope, session, r.TenantId, recipient.Address, null, 0, MailLogStatus.Rejected, reply, null, null, ct);
        }

        return reply;
    }

    public async ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope session, Stream message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var spf = _spf ?? new SpfOutcome(SpfResult.None, session.Helo);
        MimeMessage parsed;
        try
        {
            message.Position = 0;
            parsed = MimeMessage.Parse(message);
        }
        catch (InvalidDataException ex)
        {
            var bad = new SmtpReply(554, "5.6.0", "Message could not be parsed");
            await LogAllAsync(scope, session, null, message.Length, MailLogStatus.Rejected, bad, null, new { error = ex.Message }, ct);
            return bad;
        }

        var keys = new DnsDkimKeyResolver(dns);
        message.Position = 0;
        var dkim = await new DkimVerifier(keys).VerifyAsync(message, ct);
        message.Position = 0;
        var arc = await new ArcVerifier(keys).VerifyAsync(message, ct);
        var fromDomain = parsed.From.Count == 1 && parsed.From[0].Address.Contains('@', StringComparison.Ordinal)
            ? parsed.From[0].Address[(parsed.From[0].Address.LastIndexOf('@') + 1)..].ToLowerInvariant()
            : null;
        var mailFromDomain = session.From is { IsNull: false } f ? f.Domain : session.Helo;
        var dmarc = await new DmarcEvaluator(dns).EvaluateAsync(fromDomain, spf, mailFromDomain, dkim, ct);

        var auth = new
        {
            spf = spf.AuthResultsValue,
            spfDomain = spf.Domain,
            dkim = dkim.Select(d => new { result = d.AuthResultsValue, domain = d.Domain, selector = d.Selector, reason = d.Reason }),
            dmarc = dmarc.AuthResultsValue,
            dmarcPolicy = dmarc.Disposition.ToString().ToLowerInvariant(),
            fromDomain,
            arc = arc.AuthResultsValue,
        };
        var msgId = SearchText.Clean(parsed.MessageId);

        // A message without From, or with several, cannot be evaluated by DMARC and is a common spoofing vector.
        if (parsed.From.Count != 1)
        {
            var bad = new SmtpReply(550, "5.7.1", "Message must have exactly one From address (RFC 5322 3.6)");
            await LogAllAsync(scope, session, msgId, message.Length, MailLogStatus.Rejected, bad, null, auth, ct);
            return bad;
        }

        // DMARC reject policy: refuse in session. A valid ARC chain from a forwarder does not override it here;
        // trusted-forwarder overrides are a tenant setting (Phase 7).
        if (dmarc.Result == DmarcResult.Fail && dmarc.Disposition == DmarcPolicy.Reject)
        {
            var reject = new SmtpReply(550, "5.7.1", "Rejected by DMARC policy of " + dmarc.PolicyDomain);
            await LogAllAsync(scope, session, msgId, message.Length, MailLogStatus.Rejected, reject, null, auth, ct);
            return reject;
        }

        // Scanner: every leaf part that is a file (attachment, named part, or non-text body).
        var statuses = new Dictionary<string, VirusStatus>(StringComparer.Ordinal);
        var findings = new List<ScanFinding>();
        foreach (var part in parsed.Root.Walk().Where(p => p.IsLeaf && (p.IsAttachment || p.FileName is not null || !p.MediaType.StartsWith("text/", StringComparison.Ordinal))))
        {
            await using var body = part.OpenDecodedBody(message);
            var result = await scanner.ScanAsync(body, part.FileName, ct);
            statuses[part.PartPath] = result.Status switch
            {
                ScanStatus.Malicious => VirusStatus.Infected,
                ScanStatus.Suspicious => VirusStatus.Suspicious,
                _ => VirusStatus.Clean,
            };
            findings.AddRange(result.Findings.Select(x => x with { Path = part.PartPath + "/" + x.Path }));
            if (result.Status == ScanStatus.Malicious)
            {
                var reject = new SmtpReply(554, "5.7.1", "Message contains dangerous content (" + result.Findings.FirstOrDefault()?.Code + ")");
                await LogAllAsync(scope, session, msgId, message.Length, MailLogStatus.Rejected, reject, null, new { auth, findings }, ct);
                return reject;
            }
        }

        // Trace headers prepended to the stored copy (RFC 5321 4.4, RFC 8601). Forged Authentication-Results using
        // our authserv-id are not trusted (they stay in the stored message, but our header is first and the
        // UI reads only the stored auth_results column).
        var arHeader = AuthenticationResults.Build(options.AuthServId, spf, session.From?.Address, dkim, dmarc, arc);
        var received = BuildReceived(session);
        await using var final = new FileStream(Path.Combine(Path.GetTempPath(), "bm-mx-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await final.WriteAsync(Encoding.UTF8.GetBytes(arHeader + received), ct);
        message.Position = 0;
        await message.CopyToAsync(final, ct);
        final.Position = 0;
        var stored = MimeMessage.Parse(final);
        var facts = MessageFacts.Extract(stored, final);
        final.Position = 0;

        var authJson = JsonSerializer.Serialize(auth);
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var filter = scope.ServiceProvider.GetRequiredService<MxSpamFilter>();
        var htmlPart = parsed.Root.Walk().FirstOrDefault(p => p.IsLeaf && !p.IsAttachment && p.MediaType == "text/html");
        string? html = null;
        if (htmlPart is not null)
        {
            try
            {
                html = htmlPart.ReadText(message);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or DecoderFallbackException)
            {
                html = null;
            }
        }

        var (links, images) = Application.Features.Antispam.SpamInputs.HtmlFacts(html);
        var spamInput = new SpamInput
        {
            RemoteIp = MxSpamFilter.Normalize(session.RemoteAddress),
            Helo = session.Helo,
            ReverseDns = _ptr,
            Spf = spf.AuthResultsValue,
            Dkim = dkim.Select(d => (d.AuthResultsValue, d.Domain)).ToList(),
            Dmarc = dmarc.AuthResultsValue,
            DmarcPolicy = dmarc.Disposition.ToString().ToLowerInvariant(),
            Arc = arc.AuthResultsValue,
            FromAddress = parsed.From[0].Address,
            FromName = parsed.From[0].Name,
            ReplyTo = parsed.ReplyTo.FirstOrDefault()?.Address,
            Subject = parsed.Subject,
            Date = parsed.Date,
            HasMessageId = parsed.MessageId is not null,
            TextBody = facts.BodyText,
            HtmlBody = html,
            Images = images,
            Links = links,
            SuspiciousAttachmentCodes = findings.Where(x => x.Severity == ScanStatus.Suspicious).Select(x => x.Code).ToList(),
            OurHostname = options.Hostname,
        };

        // Recipients that resolve to the same mailbox (alias + primary, group + member) get one copy; each mailbox
        // gets its own verdict (the owner's Bayes model and reputation differ).
        var accepted = session.Recipients.Select(r => _recipients.GetValueOrDefault(r.Address)).Where(r => r is not null).Select(r => r!).ToList();
        var plan = new List<(Guid Tenant, MailboxVerdict Verdict)>();
        foreach (var tenant in accepted.GroupBy(r => r.TenantId))
        {
            var verdicts = await filter.ScoreAsync(tenant.Key, tenant.SelectMany(r => r.MailboxIds).Distinct().ToList(), spamInput, [.. findings.Cast<object>()], ct);
            plan.AddRange(verdicts.Select(v => (tenant.Key, v)));
        }

        // Spam above the reject threshold for every recipient is refused in session (PROMPT 2.3): the sender learns
        // it, nothing is stored. With mixed verdicts the message is accepted and the rejecting copies are quarantined.
        if (plan.Count > 0 && plan.All(p => p.Verdict.Decision == SpamDecision.Reject))
        {
            var reject = new SmtpReply(550, "5.7.1", "Message rejected as spam");
            await LogAllAsync(scope, session, msgId, message.Length, MailLogStatus.Rejected, reject, plan.Max(p => p.Verdict.Score),
                new { auth, report = JsonDocument.Parse(plan[0].Verdict.ReportJson).RootElement }, ct);
            return reject;
        }

        var rules = scope.ServiceProvider.GetRequiredService<MailboxRules>();
        var receivedAt = DateTimeOffset.UtcNow;
        var raw = new Lazy<byte[]>(() =>
        {
            var pos = final.Position;
            final.Position = 0;
            var buffer = new byte[final.Length];
            final.ReadExactly(buffer);
            final.Position = pos;
            return buffer;
        });
        // Authentication results as an ARC-Authentication-Results value, when we pass the message on.
        var authValue = arHeader[(arHeader.IndexOf(';', StringComparison.Ordinal) + 1)..].Replace("\r\n\t", " ", StringComparison.Ordinal).Trim();
        var after = new List<(Guid Tenant, Guid Mailbox, MailboxPlan Plan, IncomingMessage Incoming)>();
        try
        {
            foreach (var group in plan.GroupBy(p => (p.Tenant, Kind: p.Verdict.Decision is SpamDecision.Reject or SpamDecision.Quarantine)))
            {
                if (group.Key.Kind)
                {
                    await QuarantineAsync(scope, session, group.Key.Tenant, final, facts, group.Select(g => g.Verdict).ToList(), ct);
                    final.Position = 0;
                    continue;
                }

                foreach (var v in group.Select(g => g.Verdict))
                {
                    // Blocked senders, filters, forwarding and vacation of the mailbox (PROMPT 6.8).
                    var incoming = new IncomingMessage(facts, raw.Value, session.From is { IsNull: false } ef ? ef.Address : null, receivedAt,
                        v.Decision == SpamDecision.Spam, v.Final, options.AuthServId, authValue, arc.Status);
                    var rcpts = accepted.Where(a => a.TenantId == group.Key.Tenant && a.MailboxIds.Contains(v.MailboxId)).Select(a => a.Address).ToList();
                    var mailboxPlan = await rules.PlanAsync(group.Key.Tenant, v.MailboxId, rcpts, incoming, ct);
                    await store.DeliverAsync(new DeliveryRequest(group.Key.Tenant, final, facts, [mailboxPlan.Delivery], receivedAt,
                        authJson, v.Score, v.ReportJson, statuses), ct);
                    final.Position = 0;
                    after.Add((group.Key.Tenant, v.MailboxId, mailboxPlan, incoming));
                }
            }

            foreach (var (srsRcpt, (original, srsTenant)) in _srsBounces.Where(b => session.Recipients.Any(r => r.Address.Equals(b.Key, StringComparison.OrdinalIgnoreCase))))
            {
                // The bounce goes back to the original sender, unchanged, from the null sender.
                await scope.ServiceProvider.GetRequiredService<Application.Features.Rules.SystemMail>()
                    .SendAsync(srsTenant, null, "", original, raw.Value, notification: true, null, msgId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was acknowledged: the client keeps the message and retries (4xx), so nothing is lost.
            log.LogError(ex, "Delivery failed for message {MessageId}", msgId);
            var temp = new SmtpReply(451, "4.3.0", "Temporary storage failure, try again later");
            await LogAllAsync(scope, session, msgId, message.Length, MailLogStatus.Deferred, temp, null, auth, ct);
            return temp;
        }

        foreach (var (t, mb, mailboxPlan, incoming) in after)
        {
            await rules.AfterDeliveryAsync(t, mb, mailboxPlan, incoming, ct);
        }

        var ok = new SmtpReply(250, "2.0.0", "Message accepted for delivery");
        foreach (var r in session.Recipients)
        {
            // One log line per recipient with that recipient's outcome.
            var res = _recipients.GetValueOrDefault(r.Address);
            var verdicts = plan.Where(p => res is not null && res.MailboxIds.Contains(p.Verdict.MailboxId)).Select(p => p.Verdict).ToList();
            var status = verdicts.Any(v => v.Decision is SpamDecision.Reject or SpamDecision.Quarantine) ? MailLogStatus.Quarantined : MailLogStatus.Delivered;
            await LogAsync(scope, session, res?.TenantId ?? Guid.Empty, r.Address, msgId, final.Length, status, ok, verdicts.Count == 0 ? null : verdicts.Max(v => v.Score),
                new { auth, spam = verdicts.Select(v => v.Decision.ToString().ToUpperInvariant()) }, ct);
        }

        return ok;
    }

    /// <summary>Held for the tenant's administrators (PROMPT 6.9, 7.6): the content goes to S3, the recipients wait.</summary>
    private static async Task QuarantineAsync(IServiceScope scope, SmtpEnvelope session, Guid tenant, Stream content, MessageFacts facts,
        IReadOnlyList<MailboxVerdict> verdicts, CancellationToken ct)
    {
        var blobs = scope.ServiceProvider.GetRequiredService<BlobStore>();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var sha = await BlobStore.HashAsync(content, ct);
        if (!await blobs.ExistsAsync(tenant, sha, ct))
        {
            await blobs.PutAsync(tenant, sha, content, ct);
        }

        var top = verdicts.MaxBy(v => v.Score)!;
        db.Quarantine.Add(new QuarantinedMessage
        {
            TenantId = tenant,
            BlobSha256 = sha,
            Size = content.Length,
            EnvelopeFrom = session.From?.Address,
            Recipients = JsonSerializer.Serialize(verdicts.Select(v => v.MailboxId)),
            Subject = facts.Subject is { Length: > 998 } s ? s[..998] : facts.Subject,
            FromAddress = facts.From?.Address,
            Reason = top.Decision == SpamDecision.Reject ? "spam_score" : "suspicious_attachment",
            SpamScore = top.Score,
            Report = top.ReportJson,
            Status = QuarantineStatus.Held,
        });
        await db.SaveChangesAsync(ct);
    }

    private string BuildReceived(SmtpEnvelope s)
    {
        // "with" values from the IANA registry (RFC 3848, RFC 6531 3.7.3).
        var proto = s.SmtpUtf8 ? (s.Tls ? "UTF8SMTPS" : "UTF8SMTP") : (s.Esmtp ? "ESMTP" : "SMTP") + (s.Tls ? "S" : "");

        var host = _ptr ?? "unknown";
        var date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);
        var forClause = s.Recipients.Count == 1 ? "\r\n\tfor <" + s.Recipients[0].Address + ">" : "";
        return $"Received: from {Sanitize(s.Helo)} ({host} [{s.RemoteAddress}])\r\n\tby {options.Hostname} (BlueMail) with {proto}{forClause};\r\n\t{date}\r\n";
    }

    private static string Sanitize(string helo) =>
        new(helo.Where(c => c > 32 && c < 127 && c is not '(' and not ')' and not ';').Take(255).ToArray());

    private async Task LogAllAsync(IServiceScope scope, SmtpEnvelope s, string? msgId, long size, MailLogStatus status, SmtpReply reply, double? spam, object? details,
        CancellationToken ct)
    {
        foreach (var r in s.Recipients)
        {
            var tenant = _recipients.GetValueOrDefault(r.Address)?.TenantId ?? Guid.Empty;
            await LogAsync(scope, s, tenant, r.Address, msgId, size, status, reply, spam, details, ct);
        }
    }

    private async Task LogAsync(IServiceScope scope, SmtpEnvelope s, Guid tenant, string rcpt, string? msgId, long size, MailLogStatus status, SmtpReply reply,
        double? spam, object? details, CancellationToken ct)
    {
        if (tenant == Guid.Empty)
        {
            return; // not ours (relay attempt): logged by the process log, not in a tenant's message trace
        }

        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        db.MailLog.Add(new MailLogEntry
        {
            TenantId = tenant,
            Direction = MailDirection.Inbound,
            RfcMessageId = Trunc(msgId, 998),
            EnvelopeFrom = Trunc(s.From?.Address, 320),
            Rcpt = Trunc(rcpt, 320)!,
            Size = size,
            Status = status,
            RemoteIp = s.RemoteAddress.ToString(),
            RemoteHost = Trunc(_ptr, 255),
            SmtpResponse = Trunc(reply.Code.ToString(CultureInfo.InvariantCulture) + " " + reply.Enhanced + " " + string.Join(' ', reply.Lines), 1000),
            SpamScore = spam,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            log.LogWarning(ex, "Could not write mail_log for {Rcpt}", rcpt);
        }
    }

    private static string? Trunc(string? v, int max) => v is null ? null : v.Length <= max ? v : v[..max];
}
