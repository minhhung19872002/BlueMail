using BlueMail.Domain.Mail;

namespace BlueMail.MailStore;

/// <summary>What the caller may do with a mailbox. Granted only by <see cref="IMailStore.OpenAsync"/>.</summary>
[Flags]
public enum MailboxRights
{
    None = 0,
    Read = 1,
    Write = 2,
    SendAs = 4,
    SendOnBehalf = 8,
    Owner = Read | Write | SendAs,
}

/// <summary>
/// Proof that the caller was authorised for a mailbox. Every mail store query takes one, so there is
/// no way to read or change a mailbox without going through the access check (PROMPT 5, 8.1).
/// Only the mail store can create it.
/// </summary>
public sealed class MailboxAccess
{
    internal MailboxAccess(Guid tenantId, Guid mailboxId, Guid actorUserId, MailboxRights rights)
    {
        TenantId = tenantId;
        MailboxId = mailboxId;
        ActorUserId = actorUserId;
        Rights = rights;
    }

    public Guid TenantId { get; }
    public Guid MailboxId { get; }
    public Guid ActorUserId { get; }
    public MailboxRights Rights { get; }

    public void Require(MailboxRights needed)
    {
        if ((Rights & needed) != needed)
        {
            throw new MailboxAccessDeniedException();
        }
    }
}

public sealed class MailboxAccessDeniedException() : Exception("Access to the mailbox is denied.");

public sealed record LabelSummary(
    Guid Id,
    string? SystemKey,
    string Name,
    Guid? ParentId,
    string? ColorBg,
    string? ColorText,
    LabelVisibility ShowInList,
    bool ShowInMessageList,
    long Total,
    long Unread);

/// <summary>
/// The single entry point for mailbox data (PROMPT principle 1). The web API, SMTP and IMAP all use it;
/// nothing else writes mail.* tables. Every write bumps the mailbox modseq and publishes an event.
/// </summary>
public interface IMailStore
{
    /// <summary>Creates the mailbox of a user or shared mailbox with its system labels. Idempotent.</summary>
    Task<Guid> ProvisionMailboxAsync(Guid tenantId, Guid ownerId, MailboxOwnerKind kind, CancellationToken ct = default);

    /// <summary>
    /// Opens the user's own mailbox (<paramref name="mailboxId"/> null) or a shared mailbox the user was granted.
    /// Throws <see cref="MailboxAccessDeniedException"/> otherwise - including for mailboxes of other tenants.
    /// </summary>
    Task<MailboxAccess> OpenAsync(Guid tenantId, Guid userId, Guid? mailboxId, CancellationToken ct = default);

    /// <summary>Labels with message and unread counts computed from the rows (no stored counters).</summary>
    Task<IReadOnlyList<LabelSummary>> GetLabelsAsync(MailboxAccess access, CancellationToken ct = default);

    /// <summary>Bytes used by the mailbox, recomputed from message sizes.</summary>
    Task<long> GetUsedBytesAsync(MailboxAccess access, CancellationToken ct = default);
}
