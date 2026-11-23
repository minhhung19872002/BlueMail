using BlueMail.Domain.Common;

namespace BlueMail.Domain.Compliance;

public enum HoldScope { User, OrgUnit, Query }

/// <summary>
/// Legal hold (PROMPT 7.5). USER and ORG_UNIT holds cover every message of the mailboxes in scope, including mail that
/// arrives later; a QUERY hold covers the messages its search matched when it was placed (rows in
/// <see cref="HoldMessage"/>; docs/00 D22). <c>mail.messages.legal_hold_count</c> is recomputed from these rows.
/// </summary>
public class LegalHold : TenantEntity
{
    public string Name { get; set; } = "";
    public HoldScope Scope { get; set; }
    public Guid? UserId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public string? Query { get; set; }
    public string? Reason { get; set; }
    public int Matched { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
}

/// <summary>A message covered by a QUERY hold.</summary>
public class HoldMessage : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid HoldId { get; set; }
    public Guid MessageId { get; set; }
}

public enum RetentionAction { DeleteAfter, KeepMin }

/// <summary>
/// Retention (PROMPT 7.5): DELETE_AFTER removes messages older than <see cref="Days"/> that match the optional query;
/// KEEP_MIN keeps every message for at least <see cref="Days"/> - deleting it earlier only hides it from the user.
/// Scope: the whole tenant or one org unit.
/// </summary>
public class RetentionRule : TenantEntity
{
    public string Name { get; set; } = "";
    public RetentionAction Action { get; set; }
    public int Days { get; set; }
    public string? Query { get; set; }
    public Guid? OrgUnitId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public int LastRunDeleted { get; set; }
}

public enum ExportKind { UserData, EDiscovery, Tenant }

public enum JobStatus { Queued, Running, Done, Failed, Cancelled }

/// <summary>
/// A data export (PROMPT 7.5, 7.7): user data (mbox per label + vCard + settings), eDiscovery (mbox or .eml with a
/// SHA-256 manifest) or the whole tenant. Runs in the background; the ZIP is encrypted in bucket bm-exports and can be
/// downloaded through an expiring signed link.
/// </summary>
public class DataExport : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ExportKind Kind { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? SubjectUserId { get; set; }
    public string? Query { get; set; }
    /// <summary>eDiscovery: JSON array of mailbox ids searched (empty = all of the tenant).</summary>
    public string Mailboxes { get; set; } = "[]";
    public string Format { get; set; } = "MBOX";
    public JobStatus Status { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Chunks { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
