using BlueMail.Domain.Common;

namespace BlueMail.Domain.Tenants;

public enum TenantStatus { Active, Suspended, Trial }

public enum TenantKind
{
    /// <summary>A customer organisation.</summary>
    Customer,

    /// <summary>The single tenant holding platform operators (BlueMail service owner).</summary>
    Platform,
}

public class Tenant : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public TenantKind Kind { get; set; } = TenantKind.Customer;
    public string Plan { get; set; } = "STANDARD";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public long StorageQuotaBytes { get; set; }
    public int UserLimit { get; set; }
    public int OutboundDailyLimit { get; set; }
    public Guid? IpPoolId { get; set; }
    public string SpamSettings { get; set; } = "{}";
    public string DefaultLocale { get; set; } = "en";
    public string DefaultTimeZone { get; set; } = "UTC";

    /// <summary>While suspended, incoming mail gets 450 (sender retries) or 550 (refused) (PROMPT 7.1).</summary>
    public bool SuspendTemporary { get; set; } = true;
    public string DataRegion { get; set; } = "default";

    /// <summary>Tenant data key (AES-256), wrapped with the master key from BM_MASTER_KEY.</summary>
    public byte[] EncryptionKeyWrapped { get; set; } = [];

    public string Branding { get; set; } = "{}";
    public string? TermsUrl { get; set; }
    public string? PrivacyUrl { get; set; }
}

public class TenantDomain : TenantEntity
{
    /// <summary>A-label (xn--) form, lower case. Unique across the whole system.</summary>
    public string Domain { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string VerificationToken { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool MxOk { get; set; }
    public bool SpfOk { get; set; }
    public bool DkimOk { get; set; }
    public bool DmarcOk { get; set; }
    public bool MtaStsOk { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? CatchAllTarget { get; set; }

    /// <summary>Domain alias (PROMPT 7.2): mail to local@this-domain is delivered as local@target-domain.</summary>
    public Guid? AliasOfDomainId { get; set; }

    /// <summary>Last DNS check result per record (JSON), shown in the wizard and compared to notice breakage.</summary>
    public string? LastCheck { get; set; }
}

public enum DkimAlgorithm { Rsa2048, Ed25519 }

public enum DkimKeyStatus { Pending, Active, Retiring, Retired }

public class DkimKey : TenantEntity
{
    public Guid DomainId { get; set; }
    public string Selector { get; set; } = "";
    public DkimAlgorithm Algorithm { get; set; } = DkimAlgorithm.Rsa2048;
    public byte[] PrivateKeyEncrypted { get; set; } = [];
    public string PublicKey { get; set; } = "";
    public DkimKeyStatus Status { get; set; } = DkimKeyStatus.Pending;
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? RetireAt { get; set; }
}

public class DomainDnsCheck : TenantEntity
{
    public Guid DomainId { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string RecordType { get; set; } = "";
    public string Expected { get; set; } = "";
    public string? Actual { get; set; }
    public bool Ok { get; set; }
}
