using BlueMail.Domain.Common;

namespace BlueMail.Domain.Calendar;

/// <summary>
/// One event in a user's calendar (Outlook-like calendar, added on the owner's request): created in BlueMail, or taken
/// from an invitation the user accepted. Identified per user by its iCalendar UID; recurring events keep their RRULE
/// and are expanded when read.
/// </summary>
public class CalendarItem : TenantEntity
{
    public Guid UserId { get; set; }

    /// <summary>iCalendar UID (RFC 5545 3.8.4.7): stable across updates, shared with the other attendees.</summary>
    public string Uid { get; set; } = "";

    public int Sequence { get; set; }
    public string Summary { get; set; } = "";
    public string? Location { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public bool AllDay { get; set; }

    /// <summary>IANA zone the times were written in; recurrences step in this zone's wall time.</summary>
    public string TimeZone { get; set; } = "UTC";

    public string? RecurrenceRule { get; set; }

    /// <summary>Occurrences removed from a recurring event (EXDATE).</summary>
    public DateTimeOffset[] ExceptionDates { get; set; } = [];

    public string? OrganizerEmail { get; set; }
    public string? OrganizerName { get; set; }

    /// <summary>The user organises it (and sends invitations/updates) rather than attends.</summary>
    public bool IsOrganizer { get; set; }

    /// <summary>JSON array of { name, address, response } (response: NEEDS-ACTION, ACCEPTED, TENTATIVE, DECLINED).</summary>
    public string Attendees { get; set; } = "[]";

    /// <summary>The user's own answer when invited: ACCEPTED, TENTATIVE or DECLINED.</summary>
    public string? MyResponse { get; set; }

    /// <summary>CONFIRMED or CANCELLED (RFC 5545 STATUS).</summary>
    public string Status { get; set; } = "CONFIRMED";

    /// <summary>Minutes before the start to remind; null = no reminder.</summary>
    public int? ReminderMinutes { get; set; }

    public string? Color { get; set; }
}

/// <summary>The owner lets a colleague see (VIEW) or also change (EDIT) their calendar (Outlook "Share Calendar").</summary>
public class CalendarShare : TenantEntity
{
    public Guid OwnerUserId { get; set; }
    public Guid GranteeUserId { get; set; }

    /// <summary>VIEW or EDIT.</summary>
    public string Permission { get; set; } = "VIEW";
}
