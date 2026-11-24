using System.Globalization;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Rules;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Calendar;

public sealed record InvitePersonDto(string? Name, string Address, string? Response);

/// <summary>The invitation card of a message (PROMPT 6.3).</summary>
public sealed record InviteDto(
    string Method,
    string Uid,
    int Sequence,
    string? Summary,
    string? Location,
    string? Description,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllDay,
    string? TimeZone,
    bool Recurring,
    InvitePersonDto? Organizer,
    IReadOnlyList<InvitePersonDto> Attendees,
    int AttendeeCount,
    bool Cancelled,
    bool IAmOrganizer,
    bool CanRespond,
    string? MyResponse,
    DateTimeOffset? RespondedAt);

public static class InviteResponses
{
    public static readonly string[] All = ["ACCEPTED", "TENTATIVE", "DECLINED"];
    public const int MaxShownAttendees = 50;
}

public sealed record GetInviteQuery(Guid? MailboxId, Guid MessageId) : IRequest<InviteDto?>;

public sealed class GetInviteHandler(MailboxOpener opener, Invites invites) : IRequestHandler<GetInviteQuery, InviteDto?>
{
    public async Task<InviteDto?> Handle(GetInviteQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var loaded = await invites.LoadAsync(access, q.MessageId, ct);
        return loaded is null ? null : await invites.DtoAsync(access, loaded.Value.Event, loaded.Value.Mine, ct);
    }
}

public sealed record RespondInviteCommand(Guid? MailboxId, Guid MessageId, string Response, bool SendReply = true) : IRequest<InviteDto>;

public sealed class RespondInviteValidator : AbstractValidator<RespondInviteCommand>
{
    public RespondInviteValidator() => RuleFor(x => x.Response).Must(InviteResponses.All.Contains).WithErrorCode("invalid_response");
}

public sealed class RespondInviteHandler(MailboxOpener opener, Invites invites) : IRequestHandler<RespondInviteCommand, InviteDto>
{
    public async Task<InviteDto> Handle(RespondInviteCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        if ((access.Rights & (MailboxRights.Write | MailboxRights.SendAs)) != (MailboxRights.Write | MailboxRights.SendAs))
        {
            throw AppException.Forbidden("mailbox_read_only");
        }

        var loaded = await invites.LoadAsync(access, c.MessageId, ct) ?? throw AppException.NotFound("invite_not_found");
        return await invites.RespondAsync(access, c.MessageId, loaded.Event, loaded.Mine, c.Response, c.SendReply, ct);
    }
}

/// <summary>
/// Meeting invitations in mail (iMIP, RFC 6047): reads the event of a message, records the user's answer per event UID
/// and sends the iTIP REPLY (RFC 5546 3.2.3) to the organizer, with a copy in Sent.
/// </summary>
public sealed class Invites(PgMailStore store, SenderPolicy senders, BlueMailDbContext db, SystemMail mail, BmSettings settings)
{
    public async Task<(CalendarEvent Event, HashSet<string> Mine)?> LoadAsync(MailboxAccess access, Guid messageId, CancellationToken ct)
    {
        var m = await store.GetMessageAsync(access, messageId, ct) ?? throw AppException.NotFound("message_not_found");
        var content = await store.GetContentAsync(access, messageId, ct);
        var text = content?.Calendar;
        // Some senders attach the invitation as an .ics file instead of a text/calendar alternative; read that too.
        if (text is null && m.Attachments.FirstOrDefault(IsCalendarFile) is { } file
            && await store.GetPartAsync(access, messageId, file.PartPath, ct) is { } part && part.Content.Length <= ICalendar.MaxLength)
        {
            text = System.Text.Encoding.UTF8.GetString(part.Content);
        }

        if (text is null || ICalendar.Parse(text) is not { } e)
        {
            return null;
        }

        var mine = (await senders.OptionsAsync(access, ct)).Select(o => o.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (e, mine);
    }

    /// <summary>An attachment that holds an iCalendar object (by type or by the .ics name), not infected.</summary>
    public static bool IsCalendarFile(AttachmentInfo a) =>
        a.VirusStatus != VirusStatus.Infected && a.Size <= ICalendar.MaxLength
        && (a.ContentType.StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase) || a.ContentType.Equals("application/ics", StringComparison.OrdinalIgnoreCase)
            || (a.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false));

    public async Task<InviteDto> DtoAsync(MailboxAccess access, CalendarEvent e, HashSet<string> mine, CancellationToken ct)
    {
        var saved = await db.InviteResponses.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == access.TenantId && r.MailboxId == access.MailboxId && r.Uid == e.Uid, ct);
        var iAmOrganizer = e.Organizer is { } o && mine.Contains(o.Address);
        var meAttending = e.Attendees.FirstOrDefault(a => mine.Contains(a.Address));
        var cancelled = e.Method == "CANCEL" || e.Status == "CANCELLED";
        // An answer given to an older version of the event no longer counts once the organizer changed it.
        var myResponse = saved is not null && saved.Sequence >= e.Sequence ? saved.Response
            : meAttending?.PartStat is "ACCEPTED" or "TENTATIVE" or "DECLINED" && e.Method != "REQUEST" ? meAttending.PartStat : null;
        return new InviteDto(e.Method, e.Uid, e.Sequence, e.Summary, e.Location, e.Description, e.Start, e.End, e.AllDay, e.TimeZone, e.RecurrenceRule is not null,
            e.Organizer is { } org ? new InvitePersonDto(org.Name, org.Address, null) : null,
            e.Attendees.Take(InviteResponses.MaxShownAttendees).Select(a => new InvitePersonDto(a.Name, a.Address, a.PartStat)).ToList(), e.Attendees.Count,
            cancelled, iAmOrganizer, e.Method == "REQUEST" && !cancelled && !iAmOrganizer && e.Organizer is not null,
            myResponse, saved is not null && saved.Sequence >= e.Sequence ? saved.RespondedAt : null);
    }

    public async Task<InviteDto> RespondAsync(MailboxAccess access, Guid messageId, CalendarEvent e, HashSet<string> mine, string response, bool sendReply,
        CancellationToken ct)
    {
        if (e.Method != "REQUEST" || e.Organizer is not { } organizer || mine.Contains(organizer.Address))
        {
            throw AppException.BadRequest("invite_cannot_respond");
        }

        if (e.Method == "CANCEL" || e.Status == "CANCELLED")
        {
            throw AppException.BadRequest("invite_cancelled");
        }

        await SaveAsync(access, e, response, ct);
        if (sendReply)
        {
            await SendReplyAsync(access, messageId, e, mine, organizer, response, ct);
        }

        return await DtoAsync(access, e, mine, ct);
    }

    private async Task SaveAsync(MailboxAccess access, CalendarEvent e, string response, CancellationToken ct)
    {
        // One row per (mailbox, UID); answering again (or a newer version) overwrites it.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mail.invite_responses (id, tenant_id, mailbox_id, uid, sequence, response, responded_at)
            VALUES ({Guid.NewGuid()}, {access.TenantId}, {access.MailboxId}, {e.Uid}, {e.Sequence}, {response}, now())
            ON CONFLICT (mailbox_id, uid) DO UPDATE SET sequence = excluded.sequence, response = excluded.response, responded_at = excluded.responded_at
            """, ct);
    }

    private async Task SendReplyAsync(MailboxAccess access, Guid messageId, CalendarEvent e, HashSet<string> mine, CalendarPerson organizer, string response,
        CancellationToken ct)
    {
        var m = await store.GetMessageAsync(access, messageId, ct) ?? throw AppException.NotFound("message_not_found");
        // Answer as the address that was invited, when it is one of mine; otherwise as the address the mail came to.
        var invited = e.Attendees.FirstOrDefault(a => mine.Contains(a.Address));
        var fromAddress = invited?.Address ?? m.To.Concat(m.Cc).Select(a => a.Address).FirstOrDefault(mine.Contains) ?? mine.First();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == access.ActorUserId, ct);
        var name = invited?.Name ?? user.DisplayName;
        var culture = CultureInfo.GetCultureInfo(user.Locale ?? "en");
        var args = new Dictionary<string, object?> { ["name"] = name, ["summary"] = e.Summary ?? "" };
        var key = "invite.reply." + response.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var bytes = new MessageBuilder
        {
            From = new MailboxAddress(name, fromAddress),
            To = [new MailboxAddress(organizer.Name, organizer.Address)],
            Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".subject", culture), culture, args),
            MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            Date = now,
            InReplyTo = m.RfcMessageId,
            References = m.RfcMessageId is null ? [] : [m.RfcMessageId],
            TextBody = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".body", culture), culture, args),
            Calendar = ("REPLY", ICalendar.Reply(e, new CalendarPerson(name, fromAddress, response, null), response, now)),
        }.ToBytes();
        var signed = await mail.SignAsync(access.TenantId, fromAddress[(fromAddress.LastIndexOf('@') + 1)..], bytes, ct);
        if (!await mail.SendAsync(access.TenantId, access.ActorUserId, fromAddress, organizer.Address, signed, notification: false, user.Locale, null, ct))
        {
            throw AppException.BadRequest("invite_organizer_unreachable");
        }

        // Like Outlook: the answer is kept in Sent.
        await OutgoingDispatcher.DeliverRawAsync(store, access.TenantId, signed, new MailboxDelivery(access.MailboxId, [SystemLabels.Sent], Seen: true), ct);
    }
}
