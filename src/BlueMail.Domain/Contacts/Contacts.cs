using BlueMail.Domain.Common;

namespace BlueMail.Domain.Contacts;

/// <summary>
/// A personal contact (PROMPT 6.10). Emails and phones are JSON arrays of { value, label }. Auto-saved contacts
/// (people the user wrote to) form "Other contacts".
/// </summary>
public class Contact : TenantEntity
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Emails { get; set; } = "[]";
    public string Phones { get; set; } = "[]";
    public string? Org { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public byte[]? PhotoSha256 { get; set; }
    public string? PhotoContentType { get; set; }
    public bool IsAutoSaved { get; set; }
    public DateTimeOffset? LastContactedAt { get; set; }
    public int ContactCount { get; set; }

    /// <summary>Lower-cased addresses, for lookups by address (GIN index).</summary>
    public List<string> EmailKeys { get; set; } = [];
}

public class ContactGroup : TenantEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
}

public class ContactGroupMember : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid GroupId { get; set; }
    public Guid ContactId { get; set; }
}

/// <summary>Administrators hide people from the organisation directory (PROMPT 6.10).</summary>
public class DirectoryOverride : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool HideFromDirectory { get; set; }
}

/// <summary>Outlook "Share Contacts" within the organisation: the grantee may read the owner's contacts.</summary>
public class ContactShare : TenantEntity
{
    public Guid OwnerUserId { get; set; }
    public Guid GranteeUserId { get; set; }
}
