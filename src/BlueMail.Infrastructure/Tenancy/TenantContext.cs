namespace BlueMail.Infrastructure.Tenancy;

/// <summary>
/// The tenant the current unit of work acts for. Set once per request (from the validated access token)
/// or per job. The EF Core global filter compares every tenant-owned row with <see cref="TenantId"/>;
/// when it is null nothing matches (closed by default). Platform-wide code must opt out explicitly with
/// IgnoreQueryFilters() and is reviewed as such.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    void Set(Guid tenantId, Guid? userId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }

    public void Set(Guid tenantId, Guid? userId)
    {
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException("The tenant of a unit of work cannot change once set.");
        }

        TenantId = tenantId;
        UserId = userId;
    }
}
