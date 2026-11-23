using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Sending;

/// <summary>
/// Variable envelope return path (PROMPT 2.4): mail to other servers leaves with MAIL FROM
/// <c>bounces+&lt;message&gt;.&lt;mac&gt;@&lt;sender domain&gt;</c>, so a bounce that arrives days later names the queued
/// message it belongs to. The MAC (HMAC-SHA-256 with a key derived from the master key) stops forged addresses; the
/// domain stays the sender's, so SPF remains aligned for DMARC.
/// </summary>
public sealed class Verp
{
    public const string Prefix = "bounces+";
    private readonly byte[] _key;

    public Verp(BmSettings settings) : this(Convert.FromBase64String(settings.MasterKeyBase64))
    {
    }

    public Verp(byte[] masterKey) => _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.ASCII.GetBytes("bluemail-verp-v1"));

    public static bool IsVerp(string address) => address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public string Encode(Guid outboundMessageId, string domain) => $"{Prefix}{outboundMessageId:N}.{Mac(outboundMessageId)}@{domain.ToLowerInvariant()}";

    /// <summary>The queued message a return path names, or null when it is not one of ours.</summary>
    public Guid? Decode(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0 || !IsVerp(address))
        {
            return null;
        }

        var parts = address[Prefix.Length..at].Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id))
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1].ToLowerInvariant()), Encoding.ASCII.GetBytes(Mac(id))) ? id : null;
    }

    private string Mac(Guid id) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, id.ToByteArray()))[..12];
}

public enum ReportKind { None, DeliveryStatus, Feedback }

public sealed record ReportRecipient(string Address, string Action, string Status, string? Diagnostic);

/// <summary>What a multipart/report says (RFC 6522): a DSN (RFC 3464) or an abuse report (ARF, RFC 5965).</summary>
public sealed record ParsedReport(ReportKind Kind, IReadOnlyList<ReportRecipient> Recipients, string? FeedbackType, string? OriginalMessageId,
    string? OriginalRcpt, string? OriginalMailFrom, string? ReportingMta)
{
    public static readonly ParsedReport None = new(ReportKind.None, [], null, null, null, null, null);
}

public static class ReportParser
{
    public static ParsedReport Parse(byte[] raw)
    {
        MimeMessage msg;
        try
        {
            msg = MimeMessage.Parse(raw);
        }
        catch (InvalidDataException)
        {
            return ParsedReport.None;
        }

        var source = new MemoryStream(raw, writable: false);
        var parts = msg.Root.Walk().ToList();
        var status = parts.FirstOrDefault(p => p.MediaType is "message/delivery-status" or "message/global-delivery-status");
        var feedback = parts.FirstOrDefault(p => p.MediaType == "message/feedback-report");
        string? originalId = null, originalFrom = null;
        var original = parts.FirstOrDefault(p => p.MediaType is "message/rfc822" or "message/global" or "text/rfc822-headers" or "message/rfc822-headers");
        if (original is not null)
        {
            var headers = original.IsMessage ? original.Children[0].Headers : ParseHeaderBlock(original.ReadText(source));
            originalId = MessageIds.ParseSingle(headers.Text("Message-ID"));
            originalFrom = headers.Text("Return-Path")?.Trim().Trim('<', '>');
        }

        if (feedback is not null)
        {
            var groups = Groups(feedback.ReadText(source));
            var f = groups.FirstOrDefault() ?? [];
            var rcpt = Value(f, "Original-Rcpt-To") ?? StripType(Value(f, "Removal-Recipient"));
            return new ParsedReport(ReportKind.Feedback, [], Value(f, "Feedback-Type")?.ToLowerInvariant(), originalId, rcpt?.Trim('<', '>'),
                Value(f, "Original-Mail-From")?.Trim('<', '>') ?? originalFrom, Value(f, "Reporting-MTA") ?? Value(f, "Source"));
        }

        if (status is not null)
        {
            var groups = Groups(status.ReadText(source));
            var rcpts = new List<ReportRecipient>();
            foreach (var g in groups.Skip(1))
            {
                var address = StripType(Value(g, "Final-Recipient")) ?? StripType(Value(g, "Original-Recipient"));
                if (address is not null)
                {
                    rcpts.Add(new ReportRecipient(address.Trim('<', '>'), (Value(g, "Action") ?? "").ToLowerInvariant(), Value(g, "Status") ?? "",
                        Value(g, "Diagnostic-Code")));
                }
            }

            return new ParsedReport(ReportKind.DeliveryStatus, rcpts, null, originalId, null, originalFrom, StripType(groups.FirstOrDefault() is { } h ? Value(h, "Reporting-MTA") : null));
        }

        return ParsedReport.None;
    }

    /// <summary>"rfc822; user@example.com" -> "user@example.com".</summary>
    private static string? StripType(string? v) => v is null ? null : v.Contains(';', StringComparison.Ordinal) ? v[(v.IndexOf(';', StringComparison.Ordinal) + 1)..].Trim() : v.Trim();

    private static string? Value(Dictionary<string, string> g, string name) => g.TryGetValue(name, out var v) ? v : null;

    /// <summary>Field groups separated by blank lines (RFC 3464 2.1), with folded lines joined; the first value of a field wins.</summary>
    private static List<Dictionary<string, string>> Groups(string text)
    {
        var groups = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    groups.Add(current);
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                last = null;
                continue;
            }

            if (line[0] is ' ' or '\t' && last is not null)
            {
                current[last] += " " + line.Trim();
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                last = line[..colon].Trim();
                current.TryAdd(last, line[(colon + 1)..].Trim());
            }
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private static HeaderList ParseHeaderBlock(string text) =>
        MimeMessage.Parse(Encoding.UTF8.GetBytes(text.TrimEnd() + "\r\n\r\n")).Root.Headers;
}

/// <summary>Thresholds of the abuse self-lock (PROMPT 2.4), platform-wide; env BM_ABUSE_*.</summary>
public sealed record AbuseOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Rates are only judged after this many external recipients in the window.</summary>
    public int MinSent { get; init; } = 20;

    public double MaxHardBounceRate { get; init; } = 0.10;
    public int MinComplaints { get; init; } = 3;
    public double MaxComplaintRate { get; init; } = 0.003;
}

/// <summary>
/// Sender reputation (PROMPT 2.4): hard bounces (immediate and late) and complaints of a user's external mail in
/// the last 24 hours; above the thresholds the user's sending is locked (web, SMTP submission) until an
/// administrator lifts it, so one compromised account cannot spoil the shared IPs for everyone.
/// </summary>
public sealed class SenderReputation(BlueMailDbContext db, AuditWriter audit, AbuseOptions options, ILogger<SenderReputation> log)
{
    public sealed record Stats(int Sent, int HardBounces, int Complaints);

    public async Task<Stats> StatsAsync(Guid user, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - options.Window;
        var rows = await db.Database.SqlQuery<StatRow>($"""
            SELECT count(*)::int AS sent,
                   count(*) FILTER (WHERE r.status = 'BOUNCED' AND r.last_response ~ '^5\d\d 5\.1\.')::int AS bounced
            FROM mta.outbound_recipients r JOIN mta.outbound_messages m ON m.id = r.outbound_message_id
            WHERE m.sender_user_id = {user} AND m.created_at > {since} AND NOT m.is_notification
            """).ToListAsync(ct);
        var late = await db.FeedbackEvents.IgnoreQueryFilters().Where(e => e.SenderUserId == user && e.ReceivedAt > since)
            .GroupBy(e => e.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var asyncBounces = late.FirstOrDefault(x => x.Key == FeedbackKind.AsyncBounce)?.Count ?? 0;
        var complaints = late.FirstOrDefault(x => x.Key == FeedbackKind.Complaint)?.Count ?? 0;
        return new Stats(rows[0].Sent, rows[0].Bounced + asyncBounces, complaints);
    }

    private sealed record StatRow(int Sent, int Bounced);

    /// <summary>Locks the user's sending when over a threshold. Returns true when it locked now.</summary>
    public async Task<bool> EvaluateAsync(Guid tenant, Guid user, CancellationToken ct)
    {
        var s = await StatsAsync(user, ct);
        string? reason = null;
        if (s.Sent >= options.MinSent && (double)s.HardBounces / s.Sent > options.MaxHardBounceRate)
        {
            reason = $"hard_bounce_rate:{s.HardBounces}/{s.Sent}";
        }
        else if (s.Complaints >= options.MinComplaints && (double)s.Complaints / Math.Max(1, s.Sent) > options.MaxComplaintRate)
        {
            reason = $"complaint_rate:{s.Complaints}/{s.Sent}";
        }

        if (reason is null)
        {
            return false;
        }

        var locked = await db.Users.IgnoreQueryFilters().Where(u => u.Id == user && u.SendingLockedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SendingLockedAt, DateTimeOffset.UtcNow).SetProperty(x => x.SendingLockReason, reason), ct);
        if (locked == 0)
        {
            return false;
        }

        log.LogWarning("Sending locked for user {User}: {Reason}", user, reason);
        await audit.WriteAsync(new AuditEntry("sending.self_lock", AuditResult.Success, tenant, null, "system", Entity: "user", EntityId: user.ToString(),
            NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { reason, s.Sent, s.HardBounces, s.Complaints })), ct);
        return true;
    }

    public Task<bool> IsLockedAsync(Guid user, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == user && u.SendingLockedAt != null, ct);
}

/// <summary>
/// Late bounces and complaints arriving at MX (VERP return paths and the platform feedback address): mark the
/// recipient bounced, suppress hard-bounced or complaining addresses, count against the sender, and give the
/// sender the bounce in the Inbox.
/// </summary>
public sealed class FeedbackProcessor(IServiceProvider sp, BlueMailDbContext db, SenderReputation reputation, ILogger<FeedbackProcessor> log)
{
    /// <summary>Handles one report. <paramref name="verpMessage"/>: the queued message a VERP return path named. Returns what was recorded.</summary>
    public async Task<int> HandleAsync(byte[] raw, Guid? verpMessage, CancellationToken ct)
    {
        var report = ReportParser.Parse(raw);
        OutboundMessage? msg = null;
        if (verpMessage is { } id)
        {
            msg = await db.OutboundMessages.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        }
        else if (report.OriginalMessageId is { } rfcId)
        {
            var rcpt = report.OriginalRcpt?.ToLowerInvariant();
            msg = await db.OutboundMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.RfcMessageId == rfcId && (rcpt == null || db.OutboundRecipients.IgnoreQueryFilters().Any(r => r.OutboundMessageId == m.Id && r.Rcpt.ToLower() == rcpt)))
                .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
        }

        if (msg is null || report.Kind == ReportKind.None)
        {
            log.LogInformation("Feedback report not matched (kind {Kind}, VERP {Verp})", report.Kind, verpMessage);
            return 0;
        }

        var recorded = 0;
        if (report.Kind == ReportKind.Feedback)
        {
            var rcpt = report.OriginalRcpt ?? await db.OutboundRecipients.IgnoreQueryFilters().Where(r => r.OutboundMessageId == msg.Id).Select(r => r.Rcpt).FirstOrDefaultAsync(ct);
            if (rcpt is not null && report.FeedbackType is null or "abuse" or "fraud" or "virus")
            {
                recorded += await RecordAsync(msg, FeedbackKind.Complaint, rcpt, report.FeedbackType ?? "abuse", "complaint", report.ReportingMta, ct);
                await SuppressAsync(msg.TenantId, rcpt, "COMPLAINT", "ARF " + (report.FeedbackType ?? "abuse") + " from " + report.ReportingMta, null, ct);
            }
        }
        else
        {
            foreach (var r in report.Recipients.Where(r => r.Action == "failed" && r.Status.StartsWith('5')))
            {
                var row = await db.OutboundRecipients.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.OutboundMessageId == msg.Id && x.Rcpt.ToLower() == r.Address.ToLowerInvariant(), ct);
                if (row is null)
                {
                    continue;
                }

                if (await RecordAsync(msg, FeedbackKind.AsyncBounce, row.Rcpt, r.Status, r.Diagnostic, report.ReportingMta, ct) == 0)
                {
                    continue; // the same bounce again
                }

                recorded++;
                row.Status = RecipientDeliveryStatus.Bounced;
                row.LastResponse = Truncate($"{r.Status} (late bounce) {r.Diagnostic}", 1000);
                row.DsnSentAt ??= DateTimeOffset.UtcNow;
                db.MailLog.Add(new Domain.Admin.MailLogEntry
                {
                    TenantId = msg.TenantId, Direction = Domain.Admin.MailDirection.Outbound, RfcMessageId = msg.RfcMessageId, EnvelopeFrom = msg.EnvelopeFrom,
                    Rcpt = row.Rcpt, Size = msg.Size, Status = Domain.Admin.MailLogStatus.Bounced, SmtpResponse = row.LastResponse, RemoteHost = report.ReportingMta,
                    OccurredAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                if (r.Status.StartsWith("5.1.", StringComparison.Ordinal))
                {
                    await SuppressAsync(msg.TenantId, row.Rcpt, "HARD_BOUNCE", row.LastResponse, TimeSpan.FromDays(90), ct);
                }
            }

            if (recorded > 0)
            {
                await DeliverToSenderAsync(msg, raw, ct);
            }
        }

        if (recorded > 0 && msg.SenderUserId is { } user)
        {
            await reputation.EvaluateAsync(msg.TenantId, user, ct);
        }

        return recorded;
    }

    private async Task<int> RecordAsync(OutboundMessage msg, FeedbackKind kind, string rcpt, string? status, string? detail, string? reporter, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.feedback_events (tenant_id, sender_user_id, outbound_message_id, kind, rcpt, status, detail, reporter_domain, received_at)
            VALUES ({msg.TenantId}, {msg.SenderUserId}, {msg.Id}, {(kind == FeedbackKind.Complaint ? "COMPLAINT" : "ASYNC_BOUNCE")}, {rcpt.ToLowerInvariant()},
                    {Truncate(status, 20)}, {Truncate(detail, 1000)}, {Truncate(reporter, 253)}, now())
            ON CONFLICT (outbound_message_id, rcpt, kind) DO NOTHING
            """, ct);

    private Task SuppressAsync(Guid tenant, string address, string reason, string? detail, TimeSpan? ttl, CancellationToken ct)
    {
        DateTimeOffset? expires = ttl is { } t ? DateTimeOffset.UtcNow + t : null;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.suppressions (tenant_id, address, reason, detail, created_at, expires_at)
            VALUES ({tenant}, {address.ToLowerInvariant()}, {reason}, {Truncate(detail, 1000)}, now(), {expires})
            ON CONFLICT (tenant_id, address) DO UPDATE SET reason = excluded.reason, detail = excluded.detail, expires_at = excluded.expires_at
            """, ct);
    }

    /// <summary>The remote server's own bounce goes to the sender's Inbox, so they see why (once per message).</summary>
    private async Task DeliverToSenderAsync(OutboundMessage msg, byte[] raw, CancellationToken ct)
    {
        if (msg.SenderUserId is not { } user || msg.IsNotification)
        {
            return;
        }

        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == msg.TenantId && m.OwnerId == user && m.DeletedAt == null)
            .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (mailbox is null)
        {
            return;
        }

        await OutgoingDispatcher.DeliverRawAsync(sp.GetRequiredService<PgMailStore>(), msg.TenantId, raw,
            new MailboxDelivery(mailbox.Value, [SystemLabels.Inbox, SystemLabels.CategoryUpdates]), ct);
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
