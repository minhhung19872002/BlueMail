using System.Security.Cryptography;
using System.Text;
using BlueMail.Application.Common;
using BlueMail.Domain.Calendar;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Calendar;

public sealed record CalendarFeedDto(bool Active, string? Detail, DateTimeOffset? CreatedAt, string? Url);

public static class CalendarFeedRules
{
    public const string Busy = "BUSY", Full = "FULL";
    public static readonly TimeSpan Back = TimeSpan.FromDays(30);
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(366);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Url(BmSettings settings, string token) => settings.WebUrl + "/api/v1/calendar/feed/" + token + ".ics";
}

public sealed record GetCalendarFeedQuery : IRequest<CalendarFeedDto>;

public sealed class GetCalendarFeedHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetCalendarFeedQuery, CalendarFeedDto>
{
    public async Task<CalendarFeedDto> Handle(GetCalendarFeedQuery q, CancellationToken ct)
    {
        var f = await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.UserId, ct);
        // The link itself is shown once, when made (only its hash is kept).
        return f is null ? new CalendarFeedDto(false, null, null, null) : new CalendarFeedDto(true, f.Detail, f.CreatedAt, null);
    }
}

/// <summary>Makes a new secret link (the old one stops working) with BUSY or FULL detail; null Detail removes it.</summary>
public sealed record PublishCalendarCommand(string? Detail) : IRequest<CalendarFeedDto>;

public sealed class PublishCalendarValidator : AbstractValidator<PublishCalendarCommand>
{
    public PublishCalendarValidator() =>
        RuleFor(x => x.Detail).Must(d => d is null or CalendarFeedRules.Busy or CalendarFeedRules.Full).WithErrorCode("invalid_detail");
}

public sealed class PublishCalendarHandler(BlueMailDbContext db, ICurrentUser me, BmSettings settings) : IRequestHandler<PublishCalendarCommand, CalendarFeedDto>
{
    public async Task<CalendarFeedDto> Handle(PublishCalendarCommand c, CancellationToken ct)
    {
        var old = await db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == me.UserId, ct);
        if (old is not null)
        {
            old.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (c.Detail is null)
        {
            return new CalendarFeedDto(false, null, null, null);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var feed = new CalendarFeed { Id = Guid.NewGuid(), TenantId = me.TenantId, UserId = me.UserId, TokenHash = CalendarFeedRules.Hash(token), Detail = c.Detail };
        db.CalendarFeeds.Add(feed);
        await db.SaveChangesAsync(ct);
        return new CalendarFeedDto(true, feed.Detail, feed.CreatedAt, CalendarFeedRules.Url(settings, token));
    }
}

/// <summary>The ICS of a published calendar (anonymous: the secret token is the credential). Null for an unknown token.</summary>
public sealed record CalendarFeedIcsQuery(string Token) : IRequest<string?>;

public sealed class CalendarFeedIcsHandler(BlueMailDbContext db) : IRequestHandler<CalendarFeedIcsQuery, string?>
{
    public async Task<string?> Handle(CalendarFeedIcsQuery q, CancellationToken ct)
    {
        if (q.Token.Length is < 20 or > 100)
        {
            return null;
        }

        var hash = CalendarFeedRules.Hash(q.Token);
        // No signed-in user here: every query names the tenant and user of the feed explicitly.
        var feed = await db.CalendarFeeds.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(f => f.TokenHash == hash && f.DeletedAt == null, ct);
        if (feed is null)
        {
            return null;
        }

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == feed.UserId && u.TenantId == feed.TenantId && u.DeletedAt == null && u.Status == Domain.Identity.UserStatus.Active, ct);
        if (user is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var from = now - CalendarFeedRules.Back;
        var to = now + CalendarFeedRules.Ahead;
        var items = await db.CalendarItems.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == feed.TenantId && e.UserId == feed.UserId && e.DeletedAt == null && e.Status != "CANCELLED" && e.MyResponse != "DECLINED"
                        && e.StartAt < to && (e.RecurrenceRule != null || e.EndAt > from))
            .OrderBy(e => e.StartAt).Take(5000).ToListAsync(ct);
        var full = feed.Detail == CalendarFeedRules.Full;
        var events = items.Select(e =>
        {
            var ev = Calendars.ToEvent(e, "PUBLISH", []) with
            {
                Organizer = null,
                Summary = full ? e.Summary : "Busy",
                Location = full ? e.Location : null,
                Description = full ? e.Description : null,
            };
            return (ev, CalendarLimits.Zone(e.TimeZone));
        });
        return ICalendar.Publish(user.DisplayName, events, now);
    }
}

/// <summary>
/// Outlook "E-mail Calendar": the signed-in user's events of a date range as an ICS file to attach to a message
/// (busy times only, or with titles, places and notes). Attendees are never included.
/// </summary>
public sealed record ExportCalendarQuery(DateTimeOffset From, DateTimeOffset To, string Detail) : IRequest<string>;

public sealed class ExportCalendarValidator : AbstractValidator<ExportCalendarQuery>
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    public ExportCalendarValidator()
    {
        RuleFor(x => x.Detail).Must(d => d is CalendarFeedRules.Busy or CalendarFeedRules.Full).WithErrorCode("invalid_detail");
        RuleFor(x => x).Must(x => x.To > x.From && x.To - x.From <= MaxRange).WithErrorCode("invalid_period");
    }
}

public sealed class ExportCalendarHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ExportCalendarQuery, string>
{
    public async Task<string> Handle(ExportCalendarQuery q, CancellationToken ct)
    {
        var (from, to) = (q.From.ToUniversalTime(), q.To.ToUniversalTime());
        var user = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => u.DisplayName).FirstAsync(ct);
        var items = await db.CalendarItems.AsNoTracking()
            .Where(e => e.UserId == me.UserId && e.Status != "CANCELLED" && e.MyResponse != "DECLINED"
                        && e.StartAt < to && (e.RecurrenceRule != null || e.EndAt > from))
            .OrderBy(e => e.StartAt).Take(2000).ToListAsync(ct);
        // A series goes in only when one of its occurrences falls on the chosen days.
        items = items.Where(e => e.RecurrenceRule is null
                                 || Recurrence.Expand(e.StartAt, e.EndAt - e.StartAt, RecurrenceRule.Parse(e.RecurrenceRule), CalendarLimits.Zone(e.TimeZone), from, to, e.ExceptionDates).Count > 0)
            .ToList();
        var full = q.Detail == CalendarFeedRules.Full;
        var events = items.Select(e => (Calendars.ToEvent(e, "PUBLISH", []) with
        {
            Organizer = null,
            Summary = full ? e.Summary : "Busy",
            Location = full ? e.Location : null,
            Description = full ? e.Description : null,
        }, CalendarLimits.Zone(e.TimeZone)));
        return ICalendar.Publish(user, events, DateTimeOffset.UtcNow);
    }
}
