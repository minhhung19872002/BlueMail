using BlueMail.Domain.Common;
using BlueMail.Domain.Compliance;

namespace BlueMail.Domain.Migration;

public enum ImportSource { Imap, Mbox, Eml }

/// <summary>
/// Moving a mailbox into BlueMail (PROMPT 7.7): from an IMAP server with BlueMail's own client, or from an uploaded
/// mbox / .eml file. Imported mail keeps its date and flags, gets labels from the source folders (or Gmail labels) and
/// never runs filters, auto-replies or forwarding. Running it again imports nothing twice (<see cref="ImportedMessage"/>).
/// </summary>
public class ImportJob : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? BatchId { get; set; }
    public ImportSource Source { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string Tls { get; set; } = "IMPLICIT";
    public string? Username { get; set; }
    /// <summary>Encrypted with the tenant key; removed when the job ends.</summary>
    public byte[]? PasswordCipher { get; set; }
    public Guid? UploadId { get; set; }
    public int UploadChunks { get; set; }
    public string? FileName { get; set; }
    public JobStatus Status { get; set; }
    public int Total { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? CurrentFolder { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>One problem of an import (downloadable as CSV).</summary>
public class ImportError : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public string? Folder { get; set; }
    public long? Uid { get; set; }
    public string Code { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// What has been imported into a mailbox: by source position (account + folder + UIDVALIDITY + UID, or file + offset)
/// and by content hash. A message already imported is skipped; the same content met in another folder only gains that
/// folder's label (a copy on the source is one message here, like Gmail).
/// </summary>
public class ImportedMessage : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid MailboxId { get; set; }
    public string SourceKey { get; set; } = "";
    public byte[] ContentSha256 { get; set; } = [];
    public Guid MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
