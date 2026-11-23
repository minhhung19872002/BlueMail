using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

public enum SuspiciousAttachmentAction { Spam, Quarantine, Reject }

/// <summary>Per-tenant spam policy (PROMPT 2.3); a tenant without a row uses the defaults below.</summary>
public class SpamSettings : ITenantOwned
{
    public Guid TenantId { get; set; }
    public double SpamThreshold { get; set; } = 5.0;
    public double RejectThreshold { get; set; } = 15.0;
    public bool Greylisting { get; set; }
    public SuspiciousAttachmentAction SuspiciousAttachments { get; set; } = SuspiciousAttachmentAction.Spam;

    /// <summary>"Report phishing" sends a copy here (PROMPT 6.9); null = no copy.</summary>
    public string? PhishingReportAddress { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ListKind { Ip, Domain, Uri }

/// <summary>A DNS block list. Off by default: commercial use needs an agreement with the list operator (docs/00 Z4).</summary>
public class DnsblSource
{
    public int Id { get; set; }

    /// <summary>Null = platform-wide.</summary>
    public Guid? TenantId { get; set; }

    public string Name { get; set; } = "";
    public string Zone { get; set; } = "";
    public ListKind Kind { get; set; }
    public double Score { get; set; }
    public bool Enabled { get; set; }
    public string LicenseNote { get; set; } = "";
}

/// <summary>Bayes token counts; scope Global (scope_id empty), Tenant (tenant id) or User (user id). Tokens are 64-bit hashes.</summary>
public class BayesToken
{
    public short Scope { get; set; }
    public Guid ScopeId { get; set; }
    public long Token { get; set; }
    public int Spam { get; set; }
    public int Ham { get; set; }
}

public class BayesTotal
{
    public short Scope { get; set; }
    public Guid ScopeId { get; set; }
    public int SpamMessages { get; set; }
    public int HamMessages { get; set; }
}

/// <summary>What a mailbox already taught Bayes about a message, so reporting twice does not count twice and "not spam" undoes "spam".</summary>
public class SpamTraining : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MessageId { get; set; }
    public bool Spam { get; set; }
    public DateTimeOffset LearnedAt { get; set; }
}

/// <summary>Greylisting triplet (optional per tenant, off by default).</summary>
public class GreylistEntry
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string IpNetwork { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Recipient { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset? PassedAt { get; set; }
}

public enum QuarantineStatus { Held, Released, Deleted }

/// <summary>A message kept from the recipients for an administrator to decide (PROMPT 6.9, 7.6).</summary>
public class QuarantinedMessage : TenantEntity
{
    public byte[] BlobSha256 { get; set; } = [];
    public long Size { get; set; }
    public string? EnvelopeFrom { get; set; }
    public string Recipients { get; set; } = "[]";
    public string? Subject { get; set; }
    public string? FromAddress { get; set; }
    public string Reason { get; set; } = "";
    public double? SpamScore { get; set; }
    public string? Report { get; set; }
    public QuarantineStatus Status { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
