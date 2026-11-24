using BlueMail.Domain.Common;

namespace BlueMail.Domain.Calendar;

/// <summary>
/// A to-do of the user (Outlook "To Do", added on the owner's request): a title with optional notes, due date and
/// importance, optionally made from a mail message (the link opens the conversation).
/// </summary>
public class TaskItem : TenantEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }

    /// <summary>A calendar date (no time of day), as Outlook shows due dates.</summary>
    public DateOnly? DueDate { get; set; }

    public bool Important { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The message it was made from (same mailbox owner); its conversation opens from the task.</summary>
    public Guid? MessageId { get; set; }

    public Guid? ThreadId { get; set; }
}
