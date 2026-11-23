using BlueMail.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace BlueMail.Domain.Identity;

public enum UserStatus { Active, Suspended, Deleted }

/// <summary>
/// A person with a mailbox. Extends the ASP.NET Core Identity user so BlueID can use UserManager /
/// SignInManager; the columns follow PROMPT section 4 (identity.users).
/// </summary>
public class User : IdentityUser<Guid>, ITenantOwned, IAuditedEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }

    /// <summary>Primary address (local part may be non-ASCII, domain in A-label form). Unique system-wide.</summary>
    public string PrimaryEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public bool IsTenantAdmin { get; set; }

    /// <summary>Operators of the BlueMail service. Only users of the platform tenant may have this.</summary>
    public bool IsPlatformAdmin { get; set; }

    public long QuotaBytes { get; set; }
    public string? Locale { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>"h12" or "h23" (Intl hourCycle); null = locale default.</summary>
    public string? HourCycle { get; set; }

    /// <summary>ISO day number 1 (Monday) .. 7 (Sunday); null = locale default.</summary>
    public int? WeekStart { get; set; }

    public bool MustChangePassword { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public Guid? OrgUnitId { get; set; }
    public bool ImapEnabled { get; set; } = true;

    /// <summary>Sending stopped by the abuse self-lock (bounce/complaint rates, SenderReputation) until an admin lifts it.</summary>
    public DateTimeOffset? SendingLockedAt { get; set; }

    public string? SendingLockReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class Role : IdentityRole<Guid>
{
}

public class UserAlias : TenantEntity
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = "";
    public bool CanSendAs { get; set; } = true;
}

public class OrgUnit : TenantEntity
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
}

public enum GroupType { Distribution, Security }

public enum GroupPostPermission { Anyone, Domain, Members, Managers }

public class Group : TenantEntity
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public GroupType Type { get; set; } = GroupType.Distribution;
    public GroupPostPermission WhoCanPost { get; set; } = GroupPostPermission.Domain;
    public bool AllowExternal { get; set; }
    public bool Moderated { get; set; }
}

public enum GroupMemberRole { Owner, Manager, Member }

public class GroupMember : TenantEntity
{
    public Guid GroupId { get; set; }
    public Guid? UserId { get; set; }
    public string? ExternalEmail { get; set; }
    public GroupMemberRole Role { get; set; } = GroupMemberRole.Member;
}

public class SharedMailbox : TenantEntity
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
}

public class SharedMailboxMember : TenantEntity
{
    public Guid SharedMailboxId { get; set; }
    public Guid UserId { get; set; }
    public bool CanRead { get; set; } = true;
    public bool CanSendAs { get; set; }
    public bool CanSendOnBehalf { get; set; }
}

public class AppPassword : TenantEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class TwoFactor : TenantEntity
{
    public Guid UserId { get; set; }

    /// <summary>TOTP secret encrypted with the tenant data key (AES-GCM).</summary>
    public byte[] TotpSecretEncrypted { get; set; } = [];

    /// <summary>JSON array of Argon2id hashes of unused recovery codes.</summary>
    public string RecoveryCodesHash { get; set; } = "[]";

    /// <summary>Last accepted TOTP time step, to refuse code replay within the same window.</summary>
    public long LastUsedStep { get; set; }
}

public class Session : TenantEntity
{
    public Guid UserId { get; set; }
    public string? Device { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Support sign-in (PROMPT 7.1): the platform administrator acting as this user; such sessions end at <see cref="ExpiresAt"/>.</summary>
    public Guid? ActorUserId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
}

public enum LoginProtocol { Web, Imap, Smtp }

public enum LoginResult { Success, Failure }

public class LoginEvent : TenantEntity
{
    public Guid? UserId { get; set; }
    public LoginProtocol Protocol { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public LoginResult Result { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool IsSuspicious { get; set; }
}
