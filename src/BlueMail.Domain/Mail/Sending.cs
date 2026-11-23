using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

public class Signature : TenantEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Html { get; set; } = "";
    public bool IsDefaultNew { get; set; }
    public bool IsDefaultReply { get; set; }

    /// <summary>Default for mail sent from this alias address (null = any address).</summary>
    public string? FromAddress { get; set; }
}

public class MailTemplate : TenantEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";
}

/// <summary>
/// A draft: stable identity for the compose window; <see cref="MessageId"/> is the current stored version in the
/// Drafts label (replaced on every save, so IMAP clients see the latest).
/// </summary>
public class Draft : TenantEntity
{
    public Guid MailboxId { get; set; }
    public Guid UserId { get; set; }
    public Guid? MessageId { get; set; }

    /// <summary>Compose state (JSON): from, to/cc/bcc, subject, html, attachments, reply context.</summary>
    public string State { get; set; } = "{}";

    /// <summary>Optimistic concurrency between two windows editing the same draft.</summary>
    public int Version { get; set; }
}

public enum OutgoingStatus { UndoWindow, Scheduled, Submitting, Submitted, Cancelled, Failed }

/// <summary>
/// A send request (PROMPT 6.4). A draft can have at most one pending send (unique index on draft id while
/// UNDO_WINDOW/SCHEDULED/SUBMITTING), and every transition is one conditional UPDATE: never sent twice.
/// </summary>
public class Outgoing : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public Guid DraftId { get; set; }
    public string FromAddress { get; set; } = "";
    public OutgoingStatus Status { get; set; }
    public DateTimeOffset SendAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>RFC 5322 Message-ID, fixed when the send is requested so a retried submission is the same message.</summary>
    public string RfcMessageId { get; set; } = "";

    public string? Error { get; set; }
    public bool ArchiveThread { get; set; }
}

public class LargeAttachment : TenantEntity
{
    public Guid UserId { get; set; }
    public string ObjectKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Sha256 { get; set; } = [];
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public int DownloadCount { get; set; }
}

/// <summary>A resumable upload (tus 1.0 core subset) before it becomes an attachment.</summary>
public class Upload : TenantEntity
{
    public Guid UserId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Length { get; set; }
    public long Offset { get; set; }
    public int Chunks { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ScanStatus { get; set; }
}

public enum RecipientDeliveryStatus { Queued, Deferred, Delivered, Bounced, Expired, Suppressed }

/// <summary>A message waiting in the outbound queue (PROMPT 2.4). The signed content is a blob on S3.</summary>
public class OutboundMessage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OutgoingId { get; set; }
    public Guid? SenderUserId { get; set; }
    public byte[] BlobSha256 { get; set; } = [];
    public long Size { get; set; }
    public string EnvelopeFrom { get; set; } = "";
    public string? DkimDomain { get; set; }
    public string? RfcMessageId { get; set; }
    public bool SmtpUtf8 { get; set; }
    public bool EightBit { get; set; }
    public int Priority { get; set; }

    /// <summary>DSNs and delay notices are never answered with another DSN (RFC 3464 / RFC 5321 6.1).</summary>
    public bool IsNotification { get; set; }

    public string? SenderLocale { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OutboundRecipient : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OutboundMessageId { get; set; }
    public string Rcpt { get; set; } = "";
    public string Domain { get; set; } = "";
    public RecipientDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Lease of a worker that is delivering now; expired leases are picked up again.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastMx { get; set; }
    public string? LastIp { get; set; }
    public string? LastResponse { get; set; }
    public bool? LastTls { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? DelayNoticeAt { get; set; }
    public DateTimeOffset? DsnSentAt { get; set; }
}

public enum SuppressionReason { HardBounce, Complaint, Manual }

public class Suppression
{
    public long Id { get; set; }

    /// <summary>Null = platform-wide.</summary>
    public Guid? TenantId { get; set; }

    public string Address { get; set; } = "";
    public SuppressionReason Reason { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class DestinationLimit
{
    public int Id { get; set; }

    /// <summary>Exact domain or "*.example.com"; the most specific match wins.</summary>
    public string DomainPattern { get; set; } = "";

    public int MaxConnections { get; set; }
    public int MaxMessagesPerMinute { get; set; }
}
