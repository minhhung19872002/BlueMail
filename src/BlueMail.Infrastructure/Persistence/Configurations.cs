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
        b.Property(x => x.LastCheck).HasColumnType("jsonb");
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
        b.Property(x => x.LastUsedAt).HasDefaultValueSql("now()");
        b.HasIndex(x => x.LastUsedAt);
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

internal sealed class TrustedImageSenderConfig : IEntityTypeConfiguration<TrustedImageSender>
{
    public void Configure(EntityTypeBuilder<TrustedImageSender> b)
    {
        b.ToTable("trusted_image_senders", "mail");
        b.Property(x => x.Address).HasMaxLength(320);
        b.HasIndex(x => new { x.MailboxId, x.Address }).IsUnique().HasFilter(Sql.NotDeleted);
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SignatureConfig : IEntityTypeConfiguration<Signature>
{
    public void Configure(EntityTypeBuilder<Signature> b)
    {
        b.ToTable("signatures", "mail");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique().HasFilter(Sql.NotDeleted);
    }
}

internal sealed class MailTemplateConfig : IEntityTypeConfiguration<MailTemplate>
{
    public void Configure(EntityTypeBuilder<MailTemplate> b)
    {
        b.ToTable("templates", "mail");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique().HasFilter(Sql.NotDeleted);
    }
}

internal sealed class DraftConfig : IEntityTypeConfiguration<Draft>
{
    public void Configure(EntityTypeBuilder<Draft> b)
    {
        b.ToTable("drafts", "mail");
        b.Property(x => x.State).HasColumnType("jsonb");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.MailboxId, x.UpdatedAt });
        b.HasIndex(x => x.MessageId);
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutgoingConfig : IEntityTypeConfiguration<Outgoing>
{
    public void Configure(EntityTypeBuilder<Outgoing> b)
    {
        b.ToTable("outgoing", "mail");
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.Property(x => x.RfcMessageId).HasMaxLength(998);
        b.Property(x => x.Error).HasMaxLength(2000);
        // PROMPT 6.4: a draft is never sent twice - at most one pending send per draft, enforced by the database.
        b.HasIndex(x => x.DraftId).IsUnique().HasFilter("status IN ('UNDO_WINDOW','SCHEDULED','SUBMITTING') AND deleted_at IS NULL")
            .HasDatabaseName("ux_outgoing_draft_pending");
        b.HasIndex(x => new { x.Status, x.SendAt });
        b.HasIndex(x => new { x.MailboxId, x.Status });
    }
}

internal sealed class LargeAttachmentConfig : IEntityTypeConfiguration<LargeAttachment>
{
    public void Configure(EntityTypeBuilder<LargeAttachment> b)
    {
        b.ToTable("large_attachments", "mail");
        b.Property(x => x.ObjectKey).HasMaxLength(300);
        b.Property(x => x.FileName).HasMaxLength(1024);
        b.Property(x => x.ContentType).HasMaxLength(255);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class UploadConfig : IEntityTypeConfiguration<Upload>
{
    public void Configure(EntityTypeBuilder<Upload> b)
    {
        b.ToTable("uploads", "mail");
        b.Property(x => x.FileName).HasMaxLength(1024);
        b.Property(x => x.ContentType).HasMaxLength(255);
        b.Property(x => x.ScanStatus).HasMaxLength(32);
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class OutboundMessageConfig : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> b)
    {
        b.ToTable("outbound_messages", "mta");
        b.HasKey(x => x.Id);
        b.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        b.Property(x => x.DkimDomain).HasMaxLength(253);
        b.Property(x => x.RfcMessageId).HasMaxLength(998);
        b.Property(x => x.SenderLocale).HasMaxLength(35);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        // A submission retried after a crash cannot queue the same message twice.
        b.HasIndex(x => x.OutgoingId).IsUnique().HasFilter("outgoing_id IS NOT NULL");
    }
}

internal sealed class OutboundRecipientConfig : IEntityTypeConfiguration<OutboundRecipient>
{
    public void Configure(EntityTypeBuilder<OutboundRecipient> b)
    {
        b.ToTable("outbound_recipients", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Rcpt).HasMaxLength(320);
        b.Property(x => x.Domain).HasMaxLength(253);
        b.Property(x => x.LastMx).HasMaxLength(253);
        b.Property(x => x.LastIp).HasMaxLength(45);
        b.Property(x => x.LastResponse).HasMaxLength(1000);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });   // the SKIP LOCKED pick-up
        b.HasIndex(x => new { x.OutboundMessageId, x.Rcpt }).IsUnique();
        b.HasOne<OutboundMessage>().WithMany().HasForeignKey(x => x.OutboundMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SuppressionConfig : IEntityTypeConfiguration<Suppression>
{
    public void Configure(EntityTypeBuilder<Suppression> b)
    {
        b.ToTable("suppressions", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.HasIndex(x => new { x.TenantId, x.Address }).IsUnique().AreNullsDistinct(false);
    }
}

internal sealed class DestinationLimitConfig : IEntityTypeConfiguration<DestinationLimit>
{
    public void Configure(EntityTypeBuilder<DestinationLimit> b)
    {
        b.ToTable("destination_limits", "mta");
        b.Property(x => x.DomainPattern).HasMaxLength(253);
        b.HasIndex(x => x.DomainPattern).IsUnique();
        b.HasData(
            new DestinationLimit { Id = 1, DomainPattern = "gmail.com", MaxConnections = 5, MaxMessagesPerMinute = 300 },
            new DestinationLimit { Id = 2, DomainPattern = "outlook.com", MaxConnections = 3, MaxMessagesPerMinute = 120 },
            new DestinationLimit { Id = 3, DomainPattern = "hotmail.com", MaxConnections = 3, MaxMessagesPerMinute = 120 },
            new DestinationLimit { Id = 4, DomainPattern = "yahoo.com", MaxConnections = 3, MaxMessagesPerMinute = 120 },
            new DestinationLimit { Id = 5, DomainPattern = "*", MaxConnections = 10, MaxMessagesPerMinute = 600 });
    }
}

internal sealed class IpPoolConfig : IEntityTypeConfiguration<IpPool>
{
    public void Configure(EntityTypeBuilder<IpPool> b)
    {
        b.ToTable("ip_pools", "mta");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => x.IsDefault).IsUnique().HasFilter("is_default");
    }
}

internal sealed class SendingIpConfig : IEntityTypeConfiguration<SendingIp>
{
    public void Configure(EntityTypeBuilder<SendingIp> b)
    {
        b.ToTable("sending_ips", "mta");
        b.Property(x => x.Address).HasMaxLength(45);
        b.Property(x => x.Helo).HasMaxLength(253);
        b.HasIndex(x => x.Address).IsUnique();
        b.HasOne<IpPool>().WithMany().HasForeignKey(x => x.PoolId);
    }
}

internal sealed class SendingIpDayConfig : IEntityTypeConfiguration<SendingIpDay>
{
    public void Configure(EntityTypeBuilder<SendingIpDay> b)
    {
        b.ToTable("sending_ip_days", "mta");
        b.HasKey(x => new { x.SendingIpId, x.Day });
        b.HasOne<SendingIp>().WithMany().HasForeignKey(x => x.SendingIpId);
    }
}

internal sealed class FeedbackEventConfig : IEntityTypeConfiguration<FeedbackEvent>
{
    public void Configure(EntityTypeBuilder<FeedbackEvent> b)
    {
        b.ToTable("feedback_events", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.Rcpt).HasMaxLength(320);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.ReporterDomain).HasMaxLength(253);
        b.HasIndex(x => new { x.SenderUserId, x.ReceivedAt });
        b.HasIndex(x => new { x.OutboundMessageId, x.Rcpt, x.Kind }).IsUnique();
    }
}

internal sealed class AggregateReportConfig : IEntityTypeConfiguration<AggregateReport>
{
    public void Configure(EntityTypeBuilder<AggregateReport> b)
    {
        b.ToTable("aggregate_reports", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.Domain).HasMaxLength(253);
        b.Property(x => x.OrgName).HasMaxLength(255);
        b.Property(x => x.ReportId).HasMaxLength(255);
        b.HasIndex(x => new { x.Kind, x.OrgName, x.ReportId, x.Domain }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Kind, x.BeginAt });
    }
}

internal sealed class AggregateReportRowConfig : IEntityTypeConfiguration<AggregateReportRow>
{
    public void Configure(EntityTypeBuilder<AggregateReportRow> b)
    {
        b.ToTable("aggregate_report_rows", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Source).HasMaxLength(64);
        b.Property(x => x.Disposition).HasMaxLength(20);
        b.Property(x => x.HeaderFrom).HasMaxLength(253);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.HasOne<AggregateReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ReportId);
    }
}

internal sealed class TlsResultConfig : IEntityTypeConfiguration<TlsResult>
{
    public void Configure(EntityTypeBuilder<TlsResult> b)
    {
        b.ToTable("tls_results", "mta");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.PolicyDomain).HasMaxLength(253);
        b.Property(x => x.PolicyType).HasMaxLength(20);
        b.Property(x => x.MxHost).HasMaxLength(253);
        b.Property(x => x.ReceivingIp).HasMaxLength(45);
        b.Property(x => x.SendingIp).HasMaxLength(45);
        b.Property(x => x.FailureType).HasMaxLength(60);
        b.HasIndex(x => new { x.Day, x.PolicyDomain, x.PolicyType, x.MxHost, x.ReceivingIp, x.SendingIp, x.FailureType }).IsUnique().AreNullsDistinct(false);
    }
}

internal sealed class ConfidentialMessageConfig : IEntityTypeConfiguration<ConfidentialMessage>
{
    public void Configure(EntityTypeBuilder<ConfidentialMessage> b)
    {
        b.ToTable("confidential_messages", "mail");
        b.Property(x => x.RfcMessageId).HasMaxLength(998);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.HasIndex(x => new { x.TenantId, x.RfcMessageId }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class ConfidentialRecipientConfig : IEntityTypeConfiguration<ConfidentialRecipient>
{
    public void Configure(EntityTypeBuilder<ConfidentialRecipient> b)
    {
        b.ToTable("confidential_recipients", "mail");
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.ConfidentialMessageId, x.Address }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasOne<ConfidentialMessage>().WithMany().HasForeignKey(x => x.ConfidentialMessageId);
    }
}

internal sealed class SpamSettingsConfig : IEntityTypeConfiguration<SpamSettings>
{
    public void Configure(EntityTypeBuilder<SpamSettings> b)
    {
        b.ToTable("settings", "spam");
        b.HasKey(x => x.TenantId);
        b.Property(x => x.PhishingReportAddress).HasMaxLength(320);
    }
}

internal sealed class DnsblSourceConfig : IEntityTypeConfiguration<DnsblSource>
{
    public void Configure(EntityTypeBuilder<DnsblSource> b)
    {
        b.ToTable("dnsbl_sources", "spam");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Zone).HasMaxLength(253);
        b.Property(x => x.LicenseNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.TenantId, x.Zone }).IsUnique().AreNullsDistinct(false);
        // Known lists, all disabled: enabling one is an administrator's decision after checking its terms (docs/00 Z4).
        b.HasData(
            new DnsblSource { Id = 1, Name = "Spamhaus ZEN (DQS)", Zone = "zen.dq.spamhaus.net", Kind = ListKind.Ip, Score = 4.0, LicenseNote = "Commercial use requires a Spamhaus DQS subscription; the zone needs your DQS key prefix." },
            new DnsblSource { Id = 2, Name = "Barracuda BRBL", Zone = "b.barracudacentral.org", Kind = ListKind.Ip, Score = 2.5, LicenseNote = "Free, requires registering the querying resolver; check terms for service providers." },
            new DnsblSource { Id = 3, Name = "SpamCop", Zone = "bl.spamcop.net", Kind = ListKind.Ip, Score = 1.5, LicenseNote = "Free, use at own risk; higher false-positive rate." },
            new DnsblSource { Id = 4, Name = "PSBL", Zone = "psbl.surriel.com", Kind = ListKind.Ip, Score = 1.0, LicenseNote = "Free." },
            new DnsblSource { Id = 5, Name = "Spamhaus DBL (DQS)", Zone = "dbl.dq.spamhaus.net", Kind = ListKind.Domain, Score = 3.0, LicenseNote = "Commercial use requires a Spamhaus DQS subscription." },
            new DnsblSource { Id = 6, Name = "SURBL", Zone = "multi.surbl.org", Kind = ListKind.Uri, Score = 3.0, LicenseNote = "Commercial use requires a licence above a small free threshold." });
    }
}

internal sealed class BayesTokenConfig : IEntityTypeConfiguration<BayesToken>
{
    public void Configure(EntityTypeBuilder<BayesToken> b)
    {
        b.ToTable("bayes_tokens", "spam");
        b.HasKey(x => new { x.Scope, x.ScopeId, x.Token });
    }
}

internal sealed class BayesTotalConfig : IEntityTypeConfiguration<BayesTotal>
{
    public void Configure(EntityTypeBuilder<BayesTotal> b)
    {
        b.ToTable("bayes_totals", "spam");
        b.HasKey(x => new { x.Scope, x.ScopeId });
    }
}

internal sealed class SpamTrainingConfig : IEntityTypeConfiguration<SpamTraining>
{
    public void Configure(EntityTypeBuilder<SpamTraining> b)
    {
        b.ToTable("training", "spam");
        b.HasKey(x => new { x.TenantId, x.UserId, x.MessageId });
    }
}

internal sealed class GreylistConfig : IEntityTypeConfiguration<GreylistEntry>
{
    public void Configure(EntityTypeBuilder<GreylistEntry> b)
    {
        b.ToTable("greylist", "spam");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.IpNetwork).HasMaxLength(50);
        b.Property(x => x.Sender).HasMaxLength(320);
        b.Property(x => x.Recipient).HasMaxLength(320);
        b.HasIndex(x => new { x.TenantId, x.IpNetwork, x.Sender, x.Recipient }).IsUnique();
        b.HasIndex(x => x.FirstSeen);
    }
}

internal sealed class QuarantineConfig : IEntityTypeConfiguration<QuarantinedMessage>
{
    public void Configure(EntityTypeBuilder<QuarantinedMessage> b)
    {
        b.ToTable("quarantine", "admin");
        b.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        b.Property(x => x.Recipients).HasColumnType("jsonb");
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.Property(x => x.Reason).HasMaxLength(100);
        b.Property(x => x.Report).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
    }
}

internal sealed class MailFilterConfig : IEntityTypeConfiguration<MailFilter>
{
    public void Configure(EntityTypeBuilder<MailFilter> b)
    {
        b.ToTable("filters", "mail");
        b.Property(x => x.Criteria).HasMaxLength(2000);
        b.Property(x => x.Actions).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.MailboxId, x.SortOrder }).HasFilter("deleted_at IS NULL");
    }
}

internal sealed class FilterJobConfig : IEntityTypeConfiguration<FilterJob>
{
    public void Configure(EntityTypeBuilder<FilterJob> b)
    {
        b.ToTable("filter_jobs", "mail");
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class BulkMailJobConfig : IEntityTypeConfiguration<BulkMailJob>
{
    public void Configure(EntityTypeBuilder<BulkMailJob> b)
    {
        b.ToTable("bulk_jobs", "mail");
        b.Property(x => x.Action).HasMaxLength(32);
        b.Property(x => x.Query).HasMaxLength(2000);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

internal sealed class CalendarItemConfig : IEntityTypeConfiguration<BlueMail.Domain.Calendar.CalendarItem>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Calendar.CalendarItem> b)
    {
        b.ToTable("events", "cal");
        b.Property(x => x.Uid).HasMaxLength(255);
        b.Property(x => x.Summary).HasMaxLength(500);
        b.Property(x => x.Location).HasMaxLength(500);
        b.Property(x => x.Description).HasMaxLength(8000);
        b.Property(x => x.TimeZone).HasMaxLength(64);
        b.Property(x => x.RecurrenceRule).HasMaxLength(500);
        b.Property(x => x.OrganizerEmail).HasMaxLength(320);
        b.Property(x => x.OrganizerName).HasMaxLength(200);
        b.Property(x => x.Attendees).HasColumnType("jsonb");
        b.Property(x => x.MyResponse).HasMaxLength(16);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.Color).HasMaxLength(16);
        b.HasIndex(x => new { x.UserId, x.Uid }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => new { x.UserId, x.StartAt });
    }
}

internal sealed class TaskItemConfig : IEntityTypeConfiguration<BlueMail.Domain.Calendar.TaskItem>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Calendar.TaskItem> b)
    {
        b.ToTable("tasks", "cal");
        b.Property(x => x.Title).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(8000);
        b.Property(x => x.Category).HasMaxLength(16);
        b.HasIndex(x => new { x.UserId, x.CompletedAt, x.DueDate });
    }
}

internal sealed class ContactShareConfig : IEntityTypeConfiguration<BlueMail.Domain.Contacts.ContactShare>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Contacts.ContactShare> b)
    {
        b.ToTable("contact_shares", "contact");
        b.HasIndex(x => new { x.OwnerUserId, x.GranteeUserId }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.GranteeUserId);
    }
}

internal sealed class CalendarShareConfig : IEntityTypeConfiguration<BlueMail.Domain.Calendar.CalendarShare>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Calendar.CalendarShare> b)
    {
        b.ToTable("calendar_shares", "cal");
        b.Property(x => x.Permission).HasMaxLength(8);
        b.HasIndex(x => new { x.OwnerUserId, x.GranteeUserId }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.GranteeUserId);
    }
}

internal sealed class CalendarFeedConfig : IEntityTypeConfiguration<BlueMail.Domain.Calendar.CalendarFeed>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Calendar.CalendarFeed> b)
    {
        b.ToTable("calendar_feeds", "cal");
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(8);
        b.HasIndex(x => x.TokenHash).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("deleted_at IS NULL");
    }
}

internal sealed class InviteResponseConfig : IEntityTypeConfiguration<InviteResponse>
{
    public void Configure(EntityTypeBuilder<InviteResponse> b)
    {
        b.ToTable("invite_responses", "mail");
        b.Property(x => x.Uid).HasMaxLength(255);
        b.Property(x => x.Response).HasMaxLength(16);
        b.HasIndex(x => new { x.MailboxId, x.Uid }).IsUnique();
    }
}

internal sealed class BlockedSenderConfig : IEntityTypeConfiguration<BlockedSender>
{
    public void Configure(EntityTypeBuilder<BlockedSender> b)
    {
        b.ToTable("blocked_senders", "mail");
        b.Property(x => x.AddressOrDomain).HasMaxLength(320);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.AddressOrDomain }).IsUnique().HasFilter("deleted_at IS NULL");
    }
}

internal sealed class VacationConfig : IEntityTypeConfiguration<Vacation>
{
    public void Configure(EntityTypeBuilder<Vacation> b)
    {
        b.ToTable("vacation", "mail");
        b.HasKey(x => x.UserId);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.Html).HasMaxLength(100_000);
    }
}

internal sealed class VacationReplyConfig : IEntityTypeConfiguration<VacationReply>
{
    public void Configure(EntityTypeBuilder<VacationReply> b)
    {
        b.ToTable("vacation_replies", "mail");
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Sender).HasMaxLength(320);
        b.HasIndex(x => new { x.UserId, x.Sender }).IsUnique();
    }
}

internal sealed class ForwardingAddressConfig : IEntityTypeConfiguration<ForwardingAddress>
{
    public void Configure(EntityTypeBuilder<ForwardingAddress> b)
    {
        b.ToTable("forwarding", "mail");
        b.Property(x => x.TargetEmail).HasMaxLength(320);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.TargetEmail }).IsUnique().HasFilter("deleted_at IS NULL");
        // "Only one active automatic forward per user" is a database rule (PROMPT 0, 14).
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("is_active AND deleted_at IS NULL").HasDatabaseName("ux_forwarding_one_active");
    }
}

internal sealed class SnoozeConfig : IEntityTypeConfiguration<Snooze>
{
    public void Configure(EntityTypeBuilder<Snooze> b)
    {
        b.ToTable("snoozes", "mail");
        // "One active snooze per conversation" is a database rule.
        b.HasIndex(x => new { x.UserId, x.ThreadId }).IsUnique().HasFilter("status = 'ACTIVE'").HasDatabaseName("ux_snoozes_active");
        b.HasIndex(x => new { x.Status, x.UntilAt });
    }
}

internal sealed class UserSettingsConfig : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> b)
    {
        b.ToTable("user_settings", "mail");
        b.HasKey(x => x.UserId);
        b.Property(x => x.Settings).HasColumnType("jsonb");
    }
}

internal sealed class CategoryRuleConfig : IEntityTypeConfiguration<CategoryRule>
{
    private static readonly string[] SocialDomains =
        ["facebookmail.com", "facebook.com", "linkedin.com", "x.com", "twitter.com", "instagram.com", "zalo.me", "tiktok.com", "youtube.com", "pinterest.com"];

    private static readonly string[] PromotionDomains = ["mailchimp.com", "mcsv.net", "sendgrid.net", "shopee.vn", "lazada.vn", "tiki.vn"];

    public void Configure(EntityTypeBuilder<CategoryRule> b)
    {
        b.ToTable("category_rules", "mail");
        b.Property(x => x.Match).HasColumnType("jsonb");
        b.Property(x => x.Category).HasMaxLength(32);
        b.HasIndex(x => new { x.TenantId, x.SortOrder });
        // System defaults (tenant null): well-known social networks and bulk senders. Administrators add their own.
        var i = 0;
        b.HasData(
            SocialDomains.Select(d => new CategoryRule { Id = ++i, Match = "{\"fromDomain\":\"" + d + "\"}", Category = "CATEGORY_SOCIAL", SortOrder = i })
                .Concat(PromotionDomains.Select(d => new CategoryRule { Id = ++i, Match = "{\"fromDomain\":\"" + d + "\"}", Category = "CATEGORY_PROMOTIONS", SortOrder = i }))
                .ToArray());
    }
}

internal sealed class ContactConfig : IEntityTypeConfiguration<BlueMail.Domain.Contacts.Contact>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Contacts.Contact> b)
    {
        b.ToTable("contacts", "contact");
        b.Property(x => x.DisplayName).HasMaxLength(300);
        b.Property(x => x.Emails).HasColumnType("jsonb");
        b.Property(x => x.Phones).HasColumnType("jsonb");
        b.Property(x => x.Org).HasMaxLength(300);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(10_000);
        b.Property(x => x.PhotoContentType).HasMaxLength(50);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.DisplayName });
        b.HasIndex(x => x.EmailKeys).HasMethod("gin");
    }
}

internal sealed class ContactGroupConfig : IEntityTypeConfiguration<BlueMail.Domain.Contacts.ContactGroup>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Contacts.ContactGroup> b)
    {
        b.ToTable("contact_groups", "contact");
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
    }
}

internal sealed class ContactGroupMemberConfig : IEntityTypeConfiguration<BlueMail.Domain.Contacts.ContactGroupMember>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Contacts.ContactGroupMember> b)
    {
        b.ToTable("contact_group_members", "contact");
        b.HasKey(x => new { x.GroupId, x.ContactId });
    }
}

internal sealed class DirectoryOverrideConfig : IEntityTypeConfiguration<BlueMail.Domain.Contacts.DirectoryOverride>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Contacts.DirectoryOverride> b)
    {
        b.ToTable("directory_overrides", "contact");
        b.HasKey(x => new { x.TenantId, x.UserId });
    }
}

internal sealed class PolicyConfig : IEntityTypeConfiguration<BlueMail.Domain.Tenants.Policy>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Tenants.Policy> b)
    {
        b.ToTable("policies", "admin");
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Value).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.OrgUnitId, x.Key }).IsUnique().AreNullsDistinct(false).HasFilter("deleted_at IS NULL");
    }
}

internal sealed class RoutingRuleConfig : IEntityTypeConfiguration<BlueMail.Domain.Tenants.RoutingRule>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Tenants.RoutingRule> b)
    {
        b.ToTable("routing_rules", "admin");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Condition).HasColumnType("jsonb");
        b.Property(x => x.Action).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.Direction, x.SortOrder });
    }
}

internal sealed class SupportTicketConfig : IEntityTypeConfiguration<BlueMail.Domain.Identity.SupportTicket>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Identity.SupportTicket> b)
    {
        b.ToTable("support_tickets", "identity");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => x.TokenHash).IsUnique();
    }
}

internal sealed class LegalHoldConfig : IEntityTypeConfiguration<BlueMail.Domain.Compliance.LegalHold>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Compliance.LegalHold> b)
    {
        b.ToTable("legal_holds", "compliance");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Query).HasMaxLength(2000);
        b.Property(x => x.Reason).HasMaxLength(2000);
        b.HasIndex(x => new { x.TenantId, x.Scope }).HasFilter("released_at IS NULL AND deleted_at IS NULL");
        b.HasIndex(x => x.UserId).HasFilter("released_at IS NULL AND deleted_at IS NULL");
        b.HasIndex(x => x.OrgUnitId).HasFilter("released_at IS NULL AND deleted_at IS NULL");
    }
}

internal sealed class HoldMessageConfig : IEntityTypeConfiguration<BlueMail.Domain.Compliance.HoldMessage>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Compliance.HoldMessage> b)
    {
        b.ToTable("hold_messages", "compliance");
        b.HasKey(x => new { x.HoldId, x.MessageId });
        b.HasIndex(x => new { x.TenantId, x.MessageId });
    }
}

internal sealed class RetentionRuleConfig : IEntityTypeConfiguration<BlueMail.Domain.Compliance.RetentionRule>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Compliance.RetentionRule> b)
    {
        b.ToTable("retention_rules", "compliance");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Query).HasMaxLength(2000);
        b.HasIndex(x => new { x.TenantId, x.Action }).HasFilter("deleted_at IS NULL");
    }
}

internal sealed class DataExportConfig : IEntityTypeConfiguration<BlueMail.Domain.Compliance.DataExport>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Compliance.DataExport> b)
    {
        b.ToTable("data_exports", "compliance");
        b.Property(x => x.Query).HasMaxLength(2000);
        b.Property(x => x.Mailboxes).HasColumnType("jsonb");
        b.Property(x => x.Format).HasMaxLength(10);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.RequestedBy });
    }
}

internal sealed class ImportJobConfig : IEntityTypeConfiguration<BlueMail.Domain.Migration.ImportJob>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Migration.ImportJob> b)
    {
        b.ToTable("import_jobs", "migration");
        b.Property(x => x.Host).HasMaxLength(253);
        b.Property(x => x.Username).HasMaxLength(320);
        b.Property(x => x.Tls).HasMaxLength(10);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.CurrentFolder).HasMaxLength(1000);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.UserId });
        b.HasIndex(x => new { x.TenantId, x.BatchId });
        // One running import per target mailbox: two jobs into one mailbox would race on the dedup rows.
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("status IN ('QUEUED', 'RUNNING')").HasDatabaseName("ux_import_jobs_one_active_per_user");
    }
}

internal sealed class ImportErrorConfig : IEntityTypeConfiguration<BlueMail.Domain.Migration.ImportError>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Migration.ImportError> b)
    {
        b.ToTable("import_errors", "migration");
        b.Property(x => x.Folder).HasMaxLength(1000);
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(2000);
        b.HasIndex(x => new { x.TenantId, x.JobId });
    }
}

internal sealed class ImportedMessageConfig : IEntityTypeConfiguration<BlueMail.Domain.Migration.ImportedMessage>
{
    public void Configure(EntityTypeBuilder<BlueMail.Domain.Migration.ImportedMessage> b)
    {
        b.ToTable("imported_messages", "migration");
        b.Property(x => x.SourceKey).HasMaxLength(1500);
        b.HasIndex(x => new { x.TenantId, x.MailboxId, x.SourceKey }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MailboxId, x.ContentSha256 });
    }
}

