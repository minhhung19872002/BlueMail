using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Tenants;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlueMail.Infrastructure.Persistence;

internal static class Sql
{
    public const string NotDeleted = "deleted_at IS NULL";
}

internal sealed class TenantConfig : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("tenants", "tenant");
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Plan).HasMaxLength(32);
        b.Property(x => x.DefaultLocale).HasMaxLength(35);
        b.Property(x => x.DefaultTimeZone).HasMaxLength(64);
        b.Property(x => x.DataRegion).HasMaxLength(32);
        b.Property(x => x.SpamSettings).HasColumnType("jsonb");
        b.Property(x => x.Branding).HasColumnType("jsonb");
        b.HasIndex(x => x.Code).IsUnique().HasFilter(Sql.NotDeleted);
        // Exactly one platform tenant.
        b.HasIndex(x => x.Kind).IsUnique().HasFilter("kind = 'PLATFORM' AND deleted_at IS NULL")
            .HasDatabaseName("ux_tenants_single_platform");
    }
}

internal sealed class TenantDomainConfig : IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> b)
    {
        b.ToTable("domains", "tenant");
        b.Property(x => x.Domain).HasMaxLength(253);
        b.Property(x => x.VerificationToken).HasMaxLength(64);
        // One domain belongs to one tenant, system-wide (PROMPT 14.1).
        b.HasIndex(x => x.Domain).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasIndex(x => new { x.TenantId, x.IsPrimary }).IsUnique().HasFilter("is_primary AND deleted_at IS NULL")
            .HasDatabaseName("ux_domains_one_primary_per_tenant");
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DkimKeyConfig : IEntityTypeConfiguration<DkimKey>
{
    public void Configure(EntityTypeBuilder<DkimKey> b)
    {
        b.ToTable("dkim_keys", "tenant");
        b.Property(x => x.Selector).HasMaxLength(63);
        b.HasIndex(x => new { x.DomainId, x.Selector }).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasIndex(x => x.DomainId).IsUnique().HasFilter("status = 'ACTIVE' AND deleted_at IS NULL")
            .HasDatabaseName("ux_dkim_keys_one_active_per_domain");
        b.HasOne<TenantDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DomainDnsCheckConfig : IEntityTypeConfiguration<DomainDnsCheck>
{
    public void Configure(EntityTypeBuilder<DomainDnsCheck> b)
    {
        b.ToTable("domain_dns_checks", "tenant");
        b.Property(x => x.RecordType).HasMaxLength(16);
        b.HasIndex(x => new { x.DomainId, x.CheckedAt });
        b.HasOne<TenantDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", "identity");
        // Identity's own columns under the names of PROMPT section 4.
        b.Property(x => x.AccessFailedCount).HasColumnName("failed_login_count");
        b.Property(x => x.LockoutEnd).HasColumnName("locked_until");
        b.Property(x => x.UserName).HasMaxLength(320);
        b.Property(x => x.NormalizedUserName).HasMaxLength(320);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.NormalizedEmail).HasMaxLength(320);
        b.Property(x => x.PrimaryEmail).HasMaxLength(320);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.GivenName).HasMaxLength(100);
        b.Property(x => x.FamilyName).HasMaxLength(100);
        b.Property(x => x.Locale).HasMaxLength(35);
        b.Property(x => x.TimeZone).HasMaxLength(64);
        b.Property(x => x.HourCycle).HasMaxLength(3);
        b.HasIndex(x => x.PrimaryEmail).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasIndex(x => x.TenantId);
        // Replace Identity's default unique indexes with soft-delete aware ones.
        b.HasIndex(x => x.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique().HasFilter(Sql.NotDeleted);
        b.HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex");
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class IdentityTablesConfig :
    IEntityTypeConfiguration<Role>,
    IEntityTypeConfiguration<IdentityUserRole<Guid>>,
    IEntityTypeConfiguration<IdentityUserClaim<Guid>>,
    IEntityTypeConfiguration<IdentityUserLogin<Guid>>,
    IEntityTypeConfiguration<IdentityUserToken<Guid>>,
    IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<Role> b) => b.ToTable("roles", "identity");
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> b) => b.ToTable("user_roles", "identity");
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> b) => b.ToTable("user_claims", "identity");
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> b) => b.ToTable("user_logins", "identity");
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> b) => b.ToTable("user_tokens", "identity");
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> b) => b.ToTable("role_claims", "identity");
}

internal sealed class UserAliasConfig : IEntityTypeConfiguration<UserAlias>
{
    public void Configure(EntityTypeBuilder<UserAlias> b)
    {
        b.ToTable("user_aliases", "identity");
        b.Property(x => x.Email).HasMaxLength(320);
        b.HasIndex(x => x.Email).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrgUnitConfig : IEntityTypeConfiguration<OrgUnit>
{
    public void Configure(EntityTypeBuilder<OrgUnit> b)
    {
        b.ToTable("org_units", "identity");
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.ParentId, x.Name }).IsUnique().HasFilter(Sql.NotDeleted).AreNullsDistinct(false);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GroupConfig : IEntityTypeConfiguration<Group>, IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<Group> b)
    {
        b.ToTable("groups", "identity");
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => x.Email).IsUnique().HasFilter(Sql.NotDeleted);
    }

    public void Configure(EntityTypeBuilder<GroupMember> b)
    {
        b.ToTable("group_members", "identity");
        b.Property(x => x.ExternalEmail).HasMaxLength(320);
        b.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique().HasFilter("user_id IS NOT NULL AND deleted_at IS NULL");
        b.HasIndex(x => new { x.GroupId, x.ExternalEmail }).IsUnique().HasFilter("external_email IS NOT NULL AND deleted_at IS NULL");
        b.ToTable(t => t.HasCheckConstraint("ck_group_members_one_target", "(user_id IS NULL) <> (external_email IS NULL)"));
        b.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SharedMailboxConfig : IEntityTypeConfiguration<SharedMailbox>, IEntityTypeConfiguration<SharedMailboxMember>
{
    public void Configure(EntityTypeBuilder<SharedMailbox> b)
    {
        b.ToTable("shared_mailboxes", "identity");
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => x.Email).IsUnique().HasFilter(Sql.NotDeleted);
    }

    public void Configure(EntityTypeBuilder<SharedMailboxMember> b)
    {
        b.ToTable("shared_mailbox_members", "identity");
        b.HasIndex(x => new { x.SharedMailboxId, x.UserId }).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasOne<SharedMailbox>().WithMany().HasForeignKey(x => x.SharedMailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecurityTablesConfig :
    IEntityTypeConfiguration<AppPassword>, IEntityTypeConfiguration<TwoFactor>,
    IEntityTypeConfiguration<Session>, IEntityTypeConfiguration<LoginEvent>
{
    public void Configure(EntityTypeBuilder<AppPassword> b)
    {
        b.ToTable("app_passwords", "identity");
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<TwoFactor> b)
    {
        b.ToTable("two_factor", "identity");
        b.Property(x => x.RecoveryCodesHash).HasColumnType("jsonb");
        b.HasIndex(x => x.UserId).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions", "identity");
        b.Property(x => x.Device).HasMaxLength(200);
        b.Property(x => x.Ip).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.HasIndex(x => new { x.UserId, x.RevokedAt });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<LoginEvent> b)
    {
        b.ToTable("login_events", "identity");
        b.Property(x => x.Ip).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.Property(x => x.Reason).HasMaxLength(64);
        b.HasIndex(x => new { x.UserId, x.OccurredAt });
        b.HasIndex(x => new { x.Ip, x.OccurredAt });
    }
}

internal sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs", "audit");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Action).HasMaxLength(100);
        b.Property(x => x.Entity).HasMaxLength(100);
        b.Property(x => x.EntityId).HasMaxLength(100);
        b.Property(x => x.ActorEmail).HasMaxLength(320);
        b.Property(x => x.Ip).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.Property(x => x.OldValue).HasColumnType("jsonb");
        b.Property(x => x.NewValue).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
    }
}

// ---------------- mail store ----------------

internal sealed class MailboxConfig : IEntityTypeConfiguration<Mailbox>
{
    public void Configure(EntityTypeBuilder<Mailbox> b)
    {
        b.ToTable("mailboxes", "mail");
        b.HasIndex(x => x.OwnerId).IsUnique().HasFilter(Sql.NotDeleted);
    }
}

internal sealed class LabelConfig : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> b)
    {
        b.ToTable("labels", "mail");
        b.Property(x => x.SystemKey).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(225);
        b.Property(x => x.ColorBg).HasMaxLength(16);
        b.Property(x => x.ColorText).HasMaxLength(16);
        b.HasIndex(x => new { x.MailboxId, x.ParentId, x.Name }).IsUnique().HasFilter(Sql.NotDeleted).AreNullsDistinct(false);
        b.HasIndex(x => new { x.MailboxId, x.SystemKey }).IsUnique().HasFilter("system_key IS NOT NULL AND deleted_at IS NULL");
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Label>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BlobConfig : IEntityTypeConfiguration<Blob>
{
    public void Configure(EntityTypeBuilder<Blob> b)
    {
        b.ToTable("blobs", "mail");
        b.HasKey(x => new { x.TenantId, x.Sha256 });
        b.Property(x => x.ObjectKey).HasMaxLength(200);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

/// <summary>
/// mail.messages and mail.message_labels are hash-partitioned by tenant_id. EF does not create
/// partitioned tables, so both are excluded from generated migrations and created by hand-written SQL
/// in the migration "MailStorePartitionedTables". The model below must match that SQL.
/// </summary>
internal sealed class MessageConfig : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages", "mail", t => t.ExcludeFromMigrations());
        b.HasKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.ToAddrs).HasColumnType("jsonb");
        b.Property(x => x.CcAddrs).HasColumnType("jsonb");
        b.Property(x => x.BccAddrs).HasColumnType("jsonb");
        b.Property(x => x.ReplyTo).HasColumnType("jsonb");
        b.Property(x => x.MimeStructure).HasColumnType("jsonb");
        b.Property(x => x.SpamReport).HasColumnType("jsonb");
        b.Property(x => x.AuthResults).HasColumnType("jsonb");
        b.Property(x => x.ListUnsubscribe).HasColumnType("jsonb");
        b.Property(x => x.References).HasColumnName("references");
    }
}

internal sealed class MessageLabelConfig : IEntityTypeConfiguration<MessageLabel>
{
    public void Configure(EntityTypeBuilder<MessageLabel> b)
    {
        b.ToTable("message_labels", "mail", t => t.ExcludeFromMigrations());
        b.HasKey(x => new { x.TenantId, x.MessageId, x.LabelId });
    }
}

internal sealed class ThreadConfig : IEntityTypeConfiguration<MailThread>
{
    public void Configure(EntityTypeBuilder<MailThread> b)
    {
        b.ToTable("threads", "mail");
        b.Property(x => x.NormalizedSubject).HasMaxLength(998);
        b.HasIndex(x => new { x.MailboxId, x.LastMessageAt });
        b.HasIndex(x => new { x.MailboxId, x.NormalizedSubject });
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AttachmentConfig : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.ToTable("attachments", "mail");
        b.Property(x => x.PartPath).HasMaxLength(64);
        b.Property(x => x.FileName).HasMaxLength(1024);
        b.Property(x => x.ContentType).HasMaxLength(255);
        b.Property(x => x.ContentId).HasMaxLength(998);
        b.HasIndex(x => new { x.TenantId, x.MessageId });
    }
}

internal sealed class LabelChangeConfig : IEntityTypeConfiguration<LabelChange>
{
    public void Configure(EntityTypeBuilder<LabelChange> b)
    {
        b.ToTable("label_changes", "mail");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.HasIndex(x => new { x.LabelId, x.Modseq });
        b.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailLogConfig : IEntityTypeConfiguration<BlueMail.Domain.Admin.MailLogEntry>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Admin.MailLogEntry> b)
    {
        b.ToTable("mail_log", "admin");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.RfcMessageId).HasMaxLength(998);
        b.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        b.Property(x => x.Rcpt).HasMaxLength(320);
        b.Property(x => x.RemoteIp).HasMaxLength(45);
        b.Property(x => x.RemoteHost).HasMaxLength(255);
        b.Property(x => x.SmtpResponse).HasMaxLength(1000);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.Rcpt, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.RfcMessageId });
    }
}
