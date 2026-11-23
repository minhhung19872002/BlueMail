namespace BlueMail.Domain.Audit;

public enum AuditResult { Success, Failure, Denied }

/// <summary>
/// Append-only audit record. Each row carries the hash of the previous row (one chain for the whole
/// system), so any edit or deletion breaks verification. UPDATE/DELETE are refused by a DB trigger.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string? ActorEmail { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string Action { get; set; } = "";
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public AuditResult Result { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public byte[] PrevHash { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}
