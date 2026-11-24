using System.Globalization;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Calendar;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Calendar;

public sealed record AttendeeDto(string? Name, string Address, string? Response);

/// <summary>One occurrence of an event in the requested window (a single event has one).</summary>
public sealed record EventOccurrenceDto(
    Guid Id,
    string Uid,
    string Summary,
    string? Location,
    string? Description,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string TimeZone,
    string? RecurrenceRule,
    bool Recurring,
    bool IsOrganizer,
    string? OrganizerEmail,
    string? OrganizerName,
    IReadOnlyList<AttendeeDto> Attendees,
    string? MyResponse,
    string Status,
    int? ReminderMinutes,
    string? Color);

public static class CalendarLimits
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(400);
    public const int MaxAttendees = 100;
    public static readonly string[] Colors = ["blue", "green", "red", "purple", "orange", "teal", "gray"];
    public static readonly int[] Reminders = [0, 5, 10, 15, 30, 60, 120, 1440];

    public static TimeZoneInfo Zone(string? id) => id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.Utc;
}

// ---------- read ----------

public sealed record ListEventsQuery(DateTimeOffset From, DateTimeOffset To) : IRequest<IReadOnlyList<EventOccurrenceDto>>;

public sealed class ListEventsValidator : AbstractValidator<ListEventsQuery>
{
    public ListEventsValidator() =>
        RuleFor(x => x).Must(q => q.To > q.From && q.To - q.From <= CalendarLimits.MaxWindow).WithErrorCode("calendar_window");
}

public sealed class ListEventsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListEventsQuery, IReadOnlyList<EventOccurrenceDto>>
{
    public async Task<IReadOnlyList<EventOccurrenceDto>> Handle(ListEventsQuery q, CancellationToken ct)
    {
        // Single events overlapping the window, and every recurring event that started before its end.
        // (PostgreSQL timestamptz parameters must be UTC.)
        var from = q.From.ToUniversalTime();
        var to = q.To.ToUniversalTime();
        var items = await db.CalendarItems.AsNoTracking()
            .Where(e => e.UserId == me.UserId && e.Status != "CANCELLED" && e.MyResponse != "DECLINED" && e.StartAt < to
                        && (e.RecurrenceRule != null || e.EndAt > from))
            .OrderBy(e => e.StartAt).Take(5000).ToListAsync(ct);
        var result = new List<EventOccurrenceDto>();
        foreach (var e in items)
        {
            var duration = e.EndAt - e.StartAt;
            var rule = RecurrenceRule.Parse(e.RecurrenceRule);
            foreach (var start in Recurrence.Expand(e.StartAt, duration, rule, CalendarLimits.Zone(e.TimeZone), q.From, q.To, e.ExceptionDates))
            {
                result.Add(Calendars.Dto(e, start, start + duration));
            }
        }

        return result.OrderBy(o => o.Start).ThenBy(o => o.Summary, StringComparer.Ordinal).ToList();
    }
}

// ---------- write ----------

public sealed record AttendeeInput(string? Name, string Address);

public sealed record SaveEventCommand(
    Guid? Id,
    string Summary,
    string? Location,
    string? Description,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string? TimeZone,
    string? RecurrenceRule,
    IReadOnlyList<AttendeeInput>? Attendees,
    int? ReminderMinutes,
    string? Color) : IRequest<EventOccurrenceDto>;

public sealed class SaveEventValidator : AbstractValidator<SaveEventCommand>
{
    public SaveEventValidator()
    {
        RuleFor(x => x.Summary).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Location).MaximumLength(500);
        RuleFor(x => x.Description).MaximumLength(8000);
        RuleFor(x => x).Must(x => x.End > x.Start && x.End - x.Start <= TimeSpan.FromDays(366)).WithErrorCode("event_times");
        RuleFor(x => x.TimeZone).Must(z => z is null || TimeZoneInfo.TryFindSystemTimeZoneById(z, out _)).WithErrorCode("invalid_time_zone");
        RuleFor(x => x.RecurrenceRule).Must(r => r is null || BlueMail.Application.Features.Calendar.RecurrenceRule.Parse(r) is not null)
            .WithErrorCode("invalid_recurrence");
        RuleFor(x => x.Attendees).Must(a => a is null || a.Count <= CalendarLimits.MaxAttendees).WithErrorCode("too_many_attendees");
        RuleForEach(x => x.Attendees).Must(a => Domain.Common.EmailAddress.TryParse(a.Address, out _)).WithErrorCode("invalid_address");
        RuleFor(x => x.ReminderMinutes).Must(m => m is null || CalendarLimits.Reminders.Contains(m.Value)).WithErrorCode("invalid_reminder");
        RuleFor(x => x.Color).Must(c => c is null || CalendarLimits.Colors.Contains(c)).WithErrorCode("invalid_color");
    }
}

public sealed class SaveEventHandler(BlueMailDbContext db, ICurrentUser me, Calendars calendars) : IRequestHandler<SaveEventCommand, EventOccurrenceDto>
{
    public async Task<EventOccurrenceDto> Handle(SaveEventCommand c, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.UserId, ct);
        CalendarItem e;
        var before = new List<AttendeeDto>();
        if (c.Id is { } id)
        {
            e = await db.CalendarItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("event_not_found");
            if (!e.IsOrganizer)
            {
                // An invitation from someone else: only my own reminder and colour can change.
                e.ReminderMinutes = c.ReminderMinutes;
                e.Color = c.Color;
                await db.SaveChangesAsync(ct);
                return Calendars.Dto(e, e.StartAt, e.EndAt);
            }

            before = Calendars.People(e.Attendees);
            e.Sequence++;
        }
        else
        {
            e = new CalendarItem
            {
                Id = Guid.NewGuid(), TenantId = me.TenantId, UserId = me.UserId, Uid = Guid.NewGuid().ToString("N") + "@bluemail",
                IsOrganizer = true, OrganizerEmail = user.PrimaryEmail, OrganizerName = user.DisplayName,
            };
            db.CalendarItems.Add(e);
        }

        var zone = c.TimeZone ?? user.TimeZone ?? "UTC";
        e.Summary = c.Summary.Trim();
        e.Location = string.IsNullOrWhiteSpace(c.Location) ? null : c.Location.Trim();
        e.Description = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim();
        e.AllDay = c.AllDay;
        // All-day events are calendar dates: kept at UTC midnight so no zone moves the day.
        e.StartAt = c.AllDay ? new DateTimeOffset(c.Start.Date, TimeSpan.Zero) : c.Start.ToUniversalTime();
        e.EndAt = c.AllDay ? new DateTimeOffset(c.End.Date, TimeSpan.Zero) : c.End.ToUniversalTime();
        if (c.AllDay && e.EndAt <= e.StartAt)
        {
            e.EndAt = e.StartAt.AddDays(1);
        }

        e.TimeZone = zone;
        e.RecurrenceRule = string.IsNullOrWhiteSpace(c.RecurrenceRule) ? null : c.RecurrenceRule.Trim().ToUpperInvariant();
        e.ReminderMinutes = c.ReminderMinutes;
        e.Color = c.Color;
        var people = (c.Attendees ?? []).Where(a => !a.Address.Equals(user.PrimaryEmail, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.Address.Trim().ToLowerInvariant())
            .Select(a => new AttendeeDto(string.IsNullOrWhiteSpace(a.Name) ? null : a.Name.Trim(), a.Address.Trim().ToLowerInvariant(),
                before.FirstOrDefault(b => b.Address.Equals(a.Address.Trim(), StringComparison.OrdinalIgnoreCase))?.Response ?? "NEEDS-ACTION"))
            .ToList();
        e.Attendees = JsonSerializer.Serialize(people, Calendars.Json);
        await db.SaveChangesAsync(ct);

        // Invitations (or updates) to the guests; a guest removed from the list gets a cancellation.
        await calendars.SendAsync(user, e, "REQUEST", people, ct);
        var removed = before.Where(b => people.All(p => !p.Address.Equals(b.Address, StringComparison.OrdinalIgnoreCase))).ToList();
        await calendars.SendAsync(user, e, "CANCEL", removed, ct);
        return Calendars.Dto(e, e.StartAt, e.EndAt);
    }
}

public sealed record DeleteEventCommand(Guid Id, DateTimeOffset? Occurrence) : IRequest<bool>;

public sealed class DeleteEventHandler(BlueMailDbContext db, ICurrentUser me, Calendars calendars) : IRequestHandler<DeleteEventCommand, bool>
{
    public async Task<bool> Handle(DeleteEventCommand c, CancellationToken ct)
    {
        var e = await db.CalendarItems.FirstOrDefaultAsync(x => x.Id == c.Id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("event_not_found");
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.UserId, ct);
        if (c.Occurrence is { } at && e.RecurrenceRule is not null)
        {
            // One occurrence of a series: an EXDATE (RFC 5545 3.8.5.1); guests get the updated series.
            e.ExceptionDates = [.. e.ExceptionDates, at.ToUniversalTime()];
            e.Sequence++;
            await db.SaveChangesAsync(ct);
            if (e.IsOrganizer)
            {
                await calendars.SendAsync(user, e, "REQUEST", Calendars.People(e.Attendees), ct);
            }

            return true;
        }

        e.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (e.IsOrganizer)
        {
            e.Sequence++;
            await calendars.SendAsync(user, e, "CANCEL", Calendars.People(e.Attendees), ct);
        }

        return true;
    }
}

/// <summary>Calendar storage helpers and the iMIP mail an organizer sends (RFC 6047).</summary>
public sealed class Calendars(BlueMailDbContext db, SystemMail mail, BmSettings settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<AttendeeDto> People(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AttendeeDto>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static EventOccurrenceDto Dto(CalendarItem e, DateTimeOffset start, DateTimeOffset end) => new(
        e.Id, e.Uid, e.Summary, e.Location, e.Description, start, end, e.AllDay, e.TimeZone, e.RecurrenceRule, e.RecurrenceRule is not null, e.IsOrganizer,
        e.OrganizerEmail, e.OrganizerName, People(e.Attendees), e.MyResponse, e.Status, e.ReminderMinutes, e.Color);

    public static CalendarEvent ToEvent(CalendarItem e, string method, IReadOnlyList<AttendeeDto> attendees) => new(
        method, e.Uid, e.Sequence, e.Summary, e.Location, e.Description, e.StartAt, e.EndAt, e.AllDay, e.TimeZone, e.RecurrenceRule, null,
        e.OrganizerEmail is null ? null : new CalendarPerson(e.OrganizerName, e.OrganizerEmail, null, null),
        attendees.Select(a => new CalendarPerson(a.Name, a.Address, a.Response, null)).ToList(), e.Status)
    {
        ExceptionDates = e.ExceptionDates,
    };

    /// <summary>Sends <paramref name="method"/> (REQUEST or CANCEL) to each of <paramref name="to"/>, one message each.</summary>
    public async Task SendAsync(User organizer, CalendarItem e, string method, IReadOnlyList<AttendeeDto> to, CancellationToken ct)
    {
        if (to.Count == 0)
        {
            return;
        }

        var all = People(e.Attendees);
        var zone = CalendarLimits.Zone(e.TimeZone);
        var now = DateTimeOffset.UtcNow;
        var ics = ICalendar.Organizer(method, ToEvent(e, method, method == "CANCEL" ? to : all), zone, now);
        var culture = CultureInfo.GetCultureInfo(organizer.Locale ?? "en");
        var local = TimeZoneInfo.ConvertTime(e.StartAt, CalendarLimits.Zone(organizer.TimeZone ?? e.TimeZone));
        var args = new Dictionary<string, object?>
        {
            ["summary"] = e.Summary,
            ["name"] = organizer.DisplayName,
            ["when"] = e.AllDay ? e.StartAt.ToString("D", culture) : local.ToString("f", culture),
            ["where"] = e.Location ?? "",
        };
        var key = method == "CANCEL" ? "calendar.cancel" : "calendar.invite";
        var domain = organizer.PrimaryEmail[(organizer.PrimaryEmail.LastIndexOf('@') + 1)..];
        foreach (var a in to)
        {
            var bytes = new MessageBuilder
            {
                From = new MailboxAddress(organizer.DisplayName, organizer.PrimaryEmail),
                To = [new MailboxAddress(a.Name, a.Address)],
                Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".subject", culture), culture, args),
                MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
                Date = now,
                TextBody = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".body", culture), culture, args)
                           + (e.Description is null ? "" : "\r\n" + e.Description + "\r\n"),
                Calendar = (method, ics),
            }.ToBytes();
            var signed = await mail.SignAsync(organizer.TenantId, domain, bytes, ct);
            await mail.SendAsync(organizer.TenantId, organizer.Id, organizer.PrimaryEmail, a.Address, signed, notification: false, organizer.Locale, null, ct);
        }
    }

    /// <summary>The invitation the user answered goes into their calendar (or is updated there); "no" hides it.</summary>
    public async Task SaveAnsweredAsync(Guid tenantId, Guid userId, CalendarEvent e, string response, CancellationToken ct)
    {
        if (e.Start is not { } start)
        {
            return;
        }

        var item = await db.CalendarItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.Uid == e.Uid && x.DeletedAt == null, ct);
        if (item is null)
        {
            item = new CalendarItem { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Uid = e.Uid };
            db.CalendarItems.Add(item);
        }
        else if (item.IsOrganizer)
        {
            return; // my own event
        }

        Apply(item, e);
        item.MyResponse = response;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The organizer's copy follows a guest's REPLY; a CANCEL marks an attendee's copy cancelled.</summary>
    public async Task ApplyFromMailAsync(Guid tenantId, Guid userId, CalendarEvent e, CancellationToken ct)
    {
        var item = await db.CalendarItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.Uid == e.Uid && x.DeletedAt == null, ct);
        if (item is null)
        {
            return;
        }

        if (e.Method == "REPLY" && item.IsOrganizer)
        {
            var people = People(item.Attendees);
            var changed = false;
            foreach (var r in e.Attendees.Where(r => r.PartStat is "ACCEPTED" or "TENTATIVE" or "DECLINED"))
            {
                var i = people.FindIndex(p => p.Address.Equals(r.Address, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && people[i].Response != r.PartStat)
                {
                    people[i] = people[i] with { Response = r.PartStat };
                    changed = true;
                }
            }

            if (changed)
            {
                item.Attendees = JsonSerializer.Serialize(people, Json);
                await db.SaveChangesAsync(ct);
            }
        }
        else if (e.Method == "CANCEL" && !item.IsOrganizer && e.Sequence >= item.Sequence)
        {
            item.Status = "CANCELLED";
            await db.SaveChangesAsync(ct);
        }
        else if (e.Method == "REQUEST" && !item.IsOrganizer && e.Sequence > item.Sequence)
        {
            Apply(item, e); // an update of an event I already answered
            await db.SaveChangesAsync(ct);
        }
    }

    private static void Apply(CalendarItem item, CalendarEvent e)
    {
        item.Sequence = e.Sequence;
        item.Summary = string.IsNullOrWhiteSpace(e.Summary) ? "(?)" : e.Summary;
        item.Location = e.Location;
        item.Description = e.Description;
        item.StartAt = e.Start!.Value.ToUniversalTime();
        item.EndAt = (e.End ?? e.Start.Value).ToUniversalTime();
        item.AllDay = e.AllDay;
        item.TimeZone = e.TimeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(e.TimeZone, out var z) ? z.Id : "UTC";
        item.RecurrenceRule = RecurrenceRule.Parse(e.RecurrenceRule) is null ? null : e.RecurrenceRule!.ToUpperInvariant();
        item.ExceptionDates = [.. e.ExceptionDates.Select(x => x.ToUniversalTime())];
        item.OrganizerEmail = e.Organizer?.Address;
        item.OrganizerName = e.Organizer?.Name;
        item.IsOrganizer = false;
        item.Attendees = JsonSerializer.Serialize(e.Attendees.Select(a => new AttendeeDto(a.Name, a.Address.ToLowerInvariant(), a.PartStat)).ToList(), Json);
        item.Status = e.Method == "CANCEL" || e.Status == "CANCELLED" ? "CANCELLED" : "CONFIRMED";
    }
}

// ---------- free/busy ----------

public sealed record BusyInterval(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Busy times of one address; <see cref="Known"/> is false for addresses outside the organisation (nothing is shown).</summary>
public sealed record FreeBusyDto(string Address, bool Known, IReadOnlyList<BusyInterval> Busy);

/// <summary>
/// Outlook's Scheduling Assistant: when people of the same organisation are busy (PROMPT: shared calendars). Only
/// times are returned, never titles, places or guests; tentative answers count as busy, declined and cancelled events do not.
/// </summary>
public sealed record FreeBusyQuery(IReadOnlyList<string> Addresses, DateTimeOffset From, DateTimeOffset To) : IRequest<IReadOnlyList<FreeBusyDto>>;

public sealed class FreeBusyValidator : AbstractValidator<FreeBusyQuery>
{
    public const int MaxAddresses = 20;
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

    public FreeBusyValidator()
    {
        RuleFor(x => x.Addresses).NotEmpty().Must(a => a.Count <= MaxAddresses).WithErrorCode("too_many_attendees");
        RuleFor(x => x).Must(q => q.To > q.From && q.To - q.From <= MaxWindow).WithErrorCode("calendar_window");
    }
}

public sealed class FreeBusyHandler(BlueMailDbContext db) : IRequestHandler<FreeBusyQuery, IReadOnlyList<FreeBusyDto>>
{
    public async Task<IReadOnlyList<FreeBusyDto>> Handle(FreeBusyQuery q, CancellationToken ct)
    {
        var from = q.From.ToUniversalTime();
        var to = q.To.ToUniversalTime();
        var wanted = q.Addresses.Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
        // Same tenant only (the query filter scopes Users to the caller's tenant).
        var users = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.PrimaryEmail) && u.DeletedAt == null)
            .Select(u => new { u.Id, u.PrimaryEmail }).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var items = await db.CalendarItems.AsNoTracking()
            .Where(e => ids.Contains(e.UserId) && e.Status != "CANCELLED" && e.MyResponse != "DECLINED" && e.StartAt < to
                        && (e.RecurrenceRule != null || e.EndAt > from))
            .Take(10_000).ToListAsync(ct);
        var result = new List<FreeBusyDto>();
        foreach (var address in wanted)
        {
            var user = users.FirstOrDefault(u => u.PrimaryEmail == address);
            if (user is null)
            {
                result.Add(new FreeBusyDto(address, false, []));
                continue;
            }

            var busy = new List<BusyInterval>();
            foreach (var e in items.Where(i => i.UserId == user.Id))
            {
                var duration = e.EndAt - e.StartAt;
                foreach (var start in Recurrence.Expand(e.StartAt, duration, RecurrenceRule.Parse(e.RecurrenceRule), CalendarLimits.Zone(e.TimeZone), from, to, e.ExceptionDates))
                {
                    busy.Add(new BusyInterval(start, start + duration));
                }
            }

            result.Add(new FreeBusyDto(address, true, Merge(busy)));
        }

        return result;
    }

    /// <summary>Sorted, overlapping or touching intervals joined (so nothing about single events shows).</summary>
    public static IReadOnlyList<BusyInterval> Merge(IEnumerable<BusyInterval> intervals)
    {
        var merged = new List<BusyInterval>();
        foreach (var i in intervals.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
            {
                if (i.End > merged[^1].End)
                {
                    merged[^1] = merged[^1] with { End = i.End };
                }
            }
            else
            {
                merged.Add(i);
            }
        }

        return merged;
    }
}
