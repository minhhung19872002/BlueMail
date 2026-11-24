using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

/// <summary>
/// A filter (PROMPT 6.8): <see cref="Criteria"/> is a search query in the Gmail operator language — the same
/// parser and tree as search. <see cref="Actions"/> is JSON (FilterActions in the application layer).
/// </summary>
public class MailFilter : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public string Criteria { get; set; } = "";
    public string Actions { get; set; } = "{}";
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public enum FilterJobStatus { Queued, Running, Done, Failed }

/// <summary>"Also apply to N matching conversations" runs in the background (PROMPT 6.8).</summary>
public class FilterJob : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public Guid FilterId { get; set; }
    public FilterJobStatus Status { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>
/// A mail action on more conversations than one request may carry (PROMPT 6.3 "select all N", 8.3): the matching
/// conversations of a list or search are taken when the job starts and changed in batches in the background.
/// </summary>
public class BulkMailJob : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public string Action { get; set; } = "";
    public Guid? LabelId { get; set; }
    public Guid? FromLabelId { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }

    /// <summary>The list the selection came from: a label (null = All Mail) and an optional search.</summary>
    public Guid? ScopeLabelId { get; set; }

    public string? Query { get; set; }
    public FilterJobStatus Status { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Blocked sender: an address or a whole domain ("@domain" or "domain"). Later mail goes to Spam.</summary>
public class BlockedSender : TenantEntity
{
    public Guid UserId { get; set; }
    public string AddressOrDomain { get; set; } = "";
}

/// <summary>Out-of-office auto reply (RFC 3834).</summary>
public class Vacation : ITenantOwned
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";

    /// <summary>Only people the user has written to (contacts arrive in Phase 8 and are added then).</summary>
    public bool OnlyContacts { get; set; }

    /// <summary>Only senders of the user's own organisation.</summary>
    public bool OnlyOrganization { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One auto reply per sender per <c>VacationRules.Interval</c> (UNIQUE user + sender).</summary>
public class VacationReply : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Sender { get; set; } = "";
    public DateTimeOffset LastRepliedAt { get; set; }
}

/// <summary>
/// A forwarding address. Usable only after the code sent to it was entered (PROMPT 6.8). At most one address per
/// user is the active automatic forward (partial unique index).
/// </summary>
public class ForwardingAddress : TenantEntity
{
    public Guid UserId { get; set; }
    public string TargetEmail { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public byte[]? VerificationCodeHash { get; set; }
    public DateTimeOffset? CodeExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Keep BlueMail's copy in the Inbox; otherwise it goes to Trash after forwarding.</summary>
    public bool KeepCopy { get; set; } = true;
}

/// <summary>
/// The user's answer to a meeting invitation (PROMPT 6.3, iMIP RFC 6047): one row per mailbox and event UID, so the
/// card shows it on every copy of the invitation and an update (higher SEQUENCE) can be answered again.
/// </summary>
public class InviteResponse : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid MailboxId { get; set; }
    public string Uid { get; set; } = "";
    public int Sequence { get; set; }

    /// <summary>ACCEPTED, TENTATIVE or DECLINED (RFC 5545 PARTSTAT).</summary>
    public string Response { get; set; } = "";

    public DateTimeOffset RespondedAt { get; set; }
}
