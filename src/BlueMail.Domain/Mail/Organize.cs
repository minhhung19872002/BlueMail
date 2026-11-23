using BlueMail.Domain.Common;

namespace BlueMail.Domain.Mail;

public enum SnoozeStatus { Active, Woken, Cancelled }

/// <summary>A snoozed conversation (PROMPT 6.5): back on top of the Inbox, unread, at <see cref="UntilAt"/>.</summary>
public class Snooze : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public Guid ThreadId { get; set; }
    public DateTimeOffset UntilAt { get; set; }
    public SnoozeStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? WokenAt { get; set; }
}

/// <summary>Account settings of a user (PROMPT 6.11), one JSON document validated by the application.</summary>
public class UserSettings : ITenantOwned
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Settings { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Inbox category rule (PROMPT 6.6): tenant null = system default. <see cref="Match"/> is JSON
/// (fromDomain, header + optional value contains); the category is a CATEGORY_* key.
/// </summary>
public class CategoryRule
{
    public int Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Match { get; set; } = "{}";
    public string Category { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
}
