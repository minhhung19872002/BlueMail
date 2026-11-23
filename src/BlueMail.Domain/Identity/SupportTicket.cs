using BlueMail.Domain.Common;

namespace BlueMail.Domain.Identity;

/// <summary>A one-time ticket for a support sign-in (PROMPT 7.1), valid 10 minutes, only for the administrator who asked.</summary>
public class SupportTicket : Entity
{
    public Guid TenantId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid ActorUserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string Reason { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
