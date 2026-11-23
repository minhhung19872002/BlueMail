using System.Linq.Expressions;
using System.Text;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Common;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BlueMail.Infrastructure.Persistence;

public class BlueMailDbContext(DbContextOptions<BlueMailDbContext> options, ITenantContext tenant)
    : IdentityDbContext<User, Role, Guid>(options), IDataProtectionKeyContext
{
    private readonly ITenantContext _tenant = tenant;

    /// <summary>Used by the query filter; evaluated per query, so one context instance follows the tenant set on it.</summary>
    internal Guid? CurrentTenantId => _tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> Domains => Set<TenantDomain>();
    public DbSet<DkimKey> DkimKeys => Set<DkimKey>();
    public DbSet<DomainDnsCheck> DomainDnsChecks => Set<DomainDnsCheck>();
    public DbSet<UserAlias> UserAliases => Set<UserAlias>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<SharedMailbox> SharedMailboxes => Set<SharedMailbox>();
    public DbSet<SharedMailboxMember> SharedMailboxMembers => Set<SharedMailboxMember>();
    public DbSet<AppPassword> AppPasswords => Set<AppPassword>();
    public DbSet<TwoFactor> TwoFactors => Set<TwoFactor>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<LoginEvent> LoginEvents => Set<LoginEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<BlueMail.Domain.Admin.MailLogEntry> MailLog => Set<BlueMail.Domain.Admin.MailLogEntry>();

    /// <summary>ASP.NET Core Data Protection key ring (cookies, 2FA setup tokens), shared by all instances.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Mail store tables. Writes are only allowed from BlueMail.MailStore (enforced by a scan test).
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageLabel> MessageLabels => Set<MessageLabel>();
    public DbSet<MailThread> Threads => Set<MailThread>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<LabelChange> LabelChanges => Set<LabelChange>();
    public DbSet<TrustedImageSender> TrustedImageSenders => Set<TrustedImageSender>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(BlueMailDbContext).Assembly);
        builder.UseOpenIddict<Guid>();
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys", "identity");

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            var clr = entity.ClrType;
            if (clr.Namespace?.StartsWith("OpenIddict", StringComparison.Ordinal) == true)
            {
                entity.SetSchema("identity");
                entity.SetTableName("oidc_" + entity.GetTableName()!.Replace("OpenIddict", "", StringComparison.Ordinal).ToLowerInvariant());
            }

            if (typeof(IAuditedEntity).IsAssignableFrom(clr))
            {
                builder.Entity(clr).Property(nameof(IAuditedEntity.CreatedAt)).HasDefaultValueSql("now()");
                builder.Entity(clr).Property(nameof(IAuditedEntity.UpdatedAt)).HasDefaultValueSql("now()");
            }

            if (typeof(Entity).IsAssignableFrom(clr))
            {
                builder.Entity(clr).Property(nameof(Entity.Id)).HasDefaultValueSql("gen_random_uuid()");
            }

            // Enums are stored as UPPER_SNAKE strings (PROMPT section 4: ACTIVE|SUSPENDED|...).
            foreach (var prop in entity.GetProperties())
            {
                var enumType = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                if (enumType.IsEnum)
                {
                    var converter = (ValueConverter)Activator.CreateInstance(typeof(UpperSnakeEnumConverter<>).MakeGenericType(enumType))!;
                    prop.SetValueConverter(converter);
                    prop.SetMaxLength(32);
                }
            }

            var filter = BuildFilter(clr);
            if (filter is not null && entity.BaseType is null)
            {
                entity.SetQueryFilter(filter);
            }
        }
    }

    /// <summary>tenant_id = current AND deleted_at IS NULL, for whichever of the two interfaces the type has.</summary>
    private LambdaExpression? BuildFilter(Type clr)
    {
        var tenantOwned = typeof(ITenantOwned).IsAssignableFrom(clr);
        var softDeletable = typeof(ISoftDeletable).IsAssignableFrom(clr);
        if (!tenantOwned && !softDeletable)
        {
            return null;
        }

        var p = Expression.Parameter(clr, "e");
        Expression? body = null;
        if (tenantOwned)
        {
            var tenantId = Expression.Convert(Expression.Property(p, nameof(ITenantOwned.TenantId)), typeof(Guid?));
            var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
            body = Expression.Equal(tenantId, current);
        }

        if (softDeletable)
        {
            var notDeleted = Expression.Equal(Expression.Property(p, nameof(ISoftDeletable.DeletedAt)),
                Expression.Constant(null, typeof(DateTimeOffset?)));
            body = body is null ? notDeleted : Expression.AndAlso(body, notDeleted);
        }

        return Expression.Lambda(body!, p);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAudit()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditedEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedBy ??= _tenant.UserId;
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy ??= _tenant.UserId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy = _tenant.UserId;
            }
        }

        // A new tenant-owned row inherits the tenant of the unit of work; writing into another tenant is refused.
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            if (entry.Entity.TenantId == Guid.Empty && _tenant.TenantId is { } t)
            {
                entry.Entity.TenantId = t;
            }

            if (entry.Entity.TenantId == Guid.Empty)
            {
                throw new InvalidOperationException($"{entry.Entity.GetType().Name} has no tenant.");
            }
        }
    }
}

/// <summary>Maps CamelCase enum members to UPPER_SNAKE strings ("IfUnread" -> "IF_UNREAD").</summary>
public sealed class UpperSnakeEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => EnumNames.ToUpperSnake(v.ToString()),
    v => Enum.Parse<TEnum>(EnumNames.FromUpperSnake(v), true))
    where TEnum : struct, Enum;

public static class EnumNames
{
    public static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    public static string FromUpperSnake(string value) => value.Replace("_", "", StringComparison.Ordinal);
}
