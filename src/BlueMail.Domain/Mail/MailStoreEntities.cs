using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

// Mail store schema (PROMPT 2.5 and section 4). Only BlueMail.MailStore writes these tables.

/// <summary>One row per user mailbox or shared mailbox. <see cref="HighestModseq"/> grows on every change.</summary>
public class Mailbox : TenantEntity
{
    /// <summary>User id or shared mailbox id.</summary>
    public Guid OwnerId { get; set; }

    public MailboxOwnerKind OwnerKind { get; set; } = MailboxOwnerKind.User;
    public long HighestModseq { get; set; }

    /// <summary>Recomputed from message sizes by query; never incremented by hand (PROMPT 2.5).</summary>
    public long UsedBytes { get; set; }
}

public enum MailboxOwnerKind { User, Shared }

/// <summary>System label keys. Categories are labels too (hidden from the label list).</summary>
public static class SystemLabels
{
    public const string Inbox = "INBOX";
    public const string Sent = "SENT";
    public const string Drafts = "DRAFTS";
    public const string Spam = "SPAM";
    public const string Trash = "TRASH";
    public const string Starred = "STARRED";
    public const string Important = "IMPORTANT";
    public const string Snoozed = "SNOOZED";
    public const string Scheduled = "SCHEDULED";
    public const string CategoryPrimary = "CATEGORY_PRIMARY";
    public const string CategoryPromotions = "CATEGORY_PROMOTIONS";
    public const string CategorySocial = "CATEGORY_SOCIAL";
    public const string CategoryUpdates = "CATEGORY_UPDATES";
    public const string CategoryForums = "CATEGORY_FORUMS";

    public static readonly IReadOnlyList<string> All =
    [
        Inbox, Sent, Drafts, Spam, Trash, Starred, Important, Snoozed, Scheduled,
        CategoryPrimary, CategoryPromotions, CategorySocial, CategoryUpdates, CategoryForums,
    ];
}

public enum LabelVisibility { Show, Hide, IfUnread }

public class Label : TenantEntity
{
    public Guid MailboxId { get; set; }
    public string? SystemKey { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public string? ColorBg { get; set; }
    public string? ColorText { get; set; }
    public LabelVisibility ShowInList { get; set; } = LabelVisibility.Show;
    public bool ShowInMessageList { get; set; } = true;

    /// <summary>IMAP UIDVALIDITY (RFC 3501 2.3.1.1). Changes only if UIDs are ever reassigned.</summary>
    public long UidValidity { get; set; }

    /// <summary>Next IMAP UID; allocated atomically with UPDATE ... RETURNING.</summary>
    public long UidNext { get; set; } = 1;

    public bool ImapSubscribed { get; set; } = true;
}

/// <summary>Raw RFC 5322 content on S3, encrypted with the tenant key, deduplicated per tenant by SHA-256.</summary>
public class Blob : ITenantOwned
{
    public Guid TenantId { get; set; }
    public byte[] Sha256 { get; set; } = [];
    public string ObjectKey { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Message : TenantEntity
{
    public Guid MailboxId { get; set; }
    public byte[] BlobSha256 { get; set; } = [];
    public Guid ThreadId { get; set; }
    public string? RfcMessageId { get; set; }
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];
    public string? Subject { get; set; }
    public string? NormalizedSubject { get; set; }
    public string? FromAddr { get; set; }
    public string? FromName { get; set; }
    public string ToAddrs { get; set; } = "[]";
    public string CcAddrs { get; set; } = "[]";
    public string BccAddrs { get; set; } = "[]";
    public string ReplyTo { get; set; } = "[]";
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long Size { get; set; }
    public string? Snippet { get; set; }
    public bool HasAttachments { get; set; }
    public string MimeStructure { get; set; } = "{}";
    public bool Seen { get; set; }
    public bool Flagged { get; set; }
    public bool Answered { get; set; }
    public bool Forwarded { get; set; }
    public bool Draft { get; set; }
    public List<string> Keywords { get; set; } = [];
    public double? SpamScore { get; set; }
    public string? SpamReport { get; set; }
    public string? AuthResults { get; set; }
    public string? ListId { get; set; }
    public string? ListUnsubscribe { get; set; }
    public string? Category { get; set; }
    public bool IsImportant { get; set; }
    public long Modseq { get; set; }

    /// <summary>Recomputed from compliance.legal_holds in the same transaction as hold changes.</summary>
    public int LegalHoldCount { get; set; }

    public DateTimeOffset? ExpungedAt { get; set; }
}

public class MessageLabel : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MessageId { get; set; }
    public Guid LabelId { get; set; }
    public long ImapUid { get; set; }
    public long Modseq { get; set; }
}

public class MailThread : TenantEntity
{
    public Guid MailboxId { get; set; }
    public Guid? RootMessageId { get; set; }
    public string? NormalizedSubject { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public bool Muted { get; set; }
}

public enum VirusStatus { Clean, Infected, Suspicious, Unscanned }

public class Attachment : TenantEntity
{
    public Guid MessageId { get; set; }
    public string PartPath { get; set; } = "";
    public string? FileName { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
    public VirusStatus VirusStatus { get; set; } = VirusStatus.Unscanned;
    public byte[] Sha256 { get; set; } = [];
}

public enum LabelChangeKind { Added, Removed, Flags }

/// <summary>Change log used by IMAP QRESYNC/VANISHED and incremental web sync.</summary>
public class LabelChange : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LabelId { get; set; }
    public long ImapUid { get; set; }
    public long Modseq { get; set; }
    public LabelChangeKind Change { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
