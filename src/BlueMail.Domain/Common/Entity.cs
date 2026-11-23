namespace BlueMail.Domain.Common;

/// <summary>Columns every table carries (PROMPT section 4).</summary>
public interface IAuditedEntity
{
    DateTimeOffset CreatedAt { get; set; }
    Guid? CreatedBy { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    Guid? UpdatedBy { get; set; }
}

/// <summary>Soft-deletable rows. Unique indexes always carry WHERE deleted_at IS NULL.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Rows owned by a tenant. Filtered by the global tenant query filter.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public abstract class Entity : IAuditedEntity, ISoftDeletable
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
}
