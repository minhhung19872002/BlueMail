using BlueMail.Domain.Common;

namespace BlueMail.Domain.Tenants;

/// <summary>
/// A policy value (PROMPT 7.4): tenant-wide (OrgUnitId null) or for one organisational unit, which wins.
/// Keys are listed in the application's PolicyKeys; every key has a reader in code (a test checks).
/// </summary>
public class Policy : TenantEntity
{
    public Guid? OrgUnitId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "null";
}

public enum RoutingDirection { Inbound, Outbound }

/// <summary>
/// Administrator routing rule (PROMPT 7.4): condition JSON (senderContains, recipientContains, subjectContains,
/// hasAttachment) and action JSON (type BCC | REDIRECT | REJECT | QUARANTINE, address). Applied in order.
/// </summary>
public class RoutingRule : TenantEntity
{
    public RoutingDirection Direction { get; set; }
    public string Condition { get; set; } = "{}";
    public string Action { get; set; } = "{}";
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string Name { get; set; } = "";
}
