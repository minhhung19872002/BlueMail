using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

public enum IpPoolKind { Trial, Shared, Dedicated }

/// <summary>A group of sending addresses (PROMPT 2.4): tenants are assigned a pool by plan (Tenant.IpPoolId; null = the default pool).</summary>
public class IpPool
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public IpPoolKind Kind { get; set; }

    /// <summary>The pool of tenants without one of their own (exactly one).</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One sending IP. <see cref="Helo"/> must be the name its PTR record gives (checked before the address is used);
/// a new address is warmed up from <see cref="WarmupStartedAt"/> on a rising daily cap (WarmupSchedule).
/// </summary>
public class SendingIp
{
    public int Id { get; set; }
    public Guid PoolId { get; set; }
    public string Address { get; set; } = "";
    public string Helo { get; set; } = "";

    /// <summary>Null = fully warm (no daily cap).</summary>
    public DateTimeOffset? WarmupStartedAt { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Last PTR check: true when the reverse name equals <see cref="Helo"/> and it resolves back to the address.</summary>
    public bool? PtrOk { get; set; }

    public DateTimeOffset? PtrCheckedAt { get; set; }
}

/// <summary>Messages handed to remote servers per IP and UTC day (warm-up cap, reporting).</summary>
public class SendingIpDay
{
    public int SendingIpId { get; set; }
    public DateOnly Day { get; set; }
    public int Sent { get; set; }
}

public enum FeedbackKind { AsyncBounce, Complaint }

/// <summary>
/// A bounce that arrived after our server had handed the message over (to a VERP return path), or a complaint (ARF,
/// RFC 5965). Counted against the sender for the abuse self-lock (SenderReputation).
/// </summary>
public class FeedbackEvent : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? SenderUserId { get; set; }
    public Guid? OutboundMessageId { get; set; }
    public FeedbackKind Kind { get; set; }
    public string Rcpt { get; set; } = "";
    public string? Status { get; set; }
    public string? Detail { get; set; }
    public string? ReporterDomain { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// Outcome counters for SMTP TLS reporting (RFC 8460), per day, policy domain, policy and receiving MX; the daily
/// report is built from these rows and sent to the domain's rua (TXT _smtp._tls).
/// </summary>
public class TlsResult
{
    public long Id { get; set; }
    public DateOnly Day { get; set; }
    public string PolicyDomain { get; set; } = "";

    /// <summary>"sts", "tlsa" or "no-policy-found".</summary>
    public string PolicyType { get; set; } = "";

    public string MxHost { get; set; } = "";
    public string ReceivingIp { get; set; } = "";
    public string SendingIp { get; set; } = "";

    /// <summary>Null = success; otherwise an RFC 8460 4.3 result type (e.g. "certificate-host-mismatch", "starttls-not-supported").</summary>
    public string? FailureType { get; set; }

    public int Count { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
}

/// <summary>
/// Confidential mode (PROMPT 6.4, Phase 12): external recipients get a link to a BlueMail page instead of the
/// content; the page shows it until <see cref="ExpiresAt"/>, optionally after a one-time code sent by mail, and the
/// sender can revoke it. Screenshots and copying by the reader cannot be prevented (said in the UI).
/// </summary>
public class ConfidentialMessage : TenantEntity
{
    public Guid SenderUserId { get; set; }
    public string RfcMessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromAddress { get; set; } = "";

    /// <summary>The message as composed (RFC 5322, encrypted blob of the tenant).</summary>
    public byte[] BlobSha256 { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }
    public bool RequireCode { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>One recipient's access to a confidential message: a link token (stored hashed) and the one-time code state.</summary>
public class ConfidentialRecipient : TenantEntity
{
    public Guid ConfidentialMessageId { get; set; }
    public string Address { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? CodeHash { get; set; }
    public DateTimeOffset? CodeExpiresAt { get; set; }
    public int CodeAttempts { get; set; }
    public DateTimeOffset? FirstOpenedAt { get; set; }
    public int Opens { get; set; }
}

public enum AggregateReportKind { Dmarc, TlsRpt }

/// <summary>
/// An aggregate report another mail system sent about one of our domains (PROMPT 7.6): DMARC (RFC 7489 appendix C,
/// to the rua dmarc-reports@platform) or SMTP TLS (RFC 8460, to tls-reports@platform). One row per report; a report
/// delivered twice is kept once (organisation + report id + domain).
/// </summary>
public class AggregateReport : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public AggregateReportKind Kind { get; set; }
    public string Domain { get; set; } = "";
    public string OrgName { get; set; } = "";
    public string ReportId { get; set; } = "";
    public DateTimeOffset BeginAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// One line of a report. DMARC: messages from <see cref="Source"/> (an IP) with the given disposition and the
/// DMARC-aligned DKIM/SPF results. TLS-RPT: <see cref="Source"/> is the policy type (sts, tlsa, no-policy-found),
/// <see cref="Count"/> the successful sessions and <see cref="Failed"/> the failed ones.
/// </summary>
public class AggregateReportRow : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public long ReportId { get; set; }
    public string Source { get; set; } = "";
    public long Count { get; set; }
    public long Failed { get; set; }
    public string? Disposition { get; set; }
    public bool? DkimPass { get; set; }
    public bool? SpfPass { get; set; }
    public string? HeaderFrom { get; set; }
    public string? Detail { get; set; }
}
