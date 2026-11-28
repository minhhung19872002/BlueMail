using System.Globalization;
using System.Text;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Calendar;
using BlueMail.Application.Features.Rules;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Mail;

/// <summary>SENT (a receipt went out), DECLINED (marked without sending) or ALREADY (answered before, by any client).</summary>
public sealed record ReadReceiptResult(string Outcome);

/// <summary>
/// Answer a read receipt request (Outlook "The sender has requested a read receipt"): send = true sends an MDN,
/// false declines. Automatic is the ALWAYS setting (the disposition mode then says so, RFC 8098 3.2.6).
/// </summary>
public sealed record AnswerReadReceiptCommand(Guid? MailboxId, Guid MessageId, bool Send, bool Automatic = false) : IRequest<ReadReceiptResult>;

public sealed class AnswerReadReceiptHandler(MailboxOpener opener, PgMailStore store, SenderPolicy senders, BlueMailDbContext db, SystemMail mail,
    BmSettings settings) : IRequestHandler<AnswerReadReceiptCommand, ReadReceiptResult>
{
    public async Task<ReadReceiptResult> Handle(AnswerReadReceiptCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        if ((access.Rights & (MailboxRights.Write | MailboxRights.SendAs)) != (MailboxRights.Write | MailboxRights.SendAs))
        {
            throw AppException.Forbidden("mailbox_read_only");
        }

        var m = await store.GetMessageAsync(access, c.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        if (m.ReadReceiptTo is not { } to)
        {
            throw AppException.BadRequest("read_receipt_not_requested");
        }

        if (m.ReadReceiptSent)
        {
            return new ReadReceiptResult("ALREADY");
        }

        // Mail in Spam never confirms the address is read (a receipt is what a spammer wants).
        var spam = await db.Labels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == access.TenantId && l.MailboxId == access.MailboxId && l.SystemKey == SystemLabels.Spam && l.DeletedAt == null)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        if (c.Send && spam is { } s && m.LabelIds.Contains(s))
        {
            throw AppException.BadRequest("read_receipt_spam");
        }

        var mine = (await senders.OptionsAsync(access, ct)).Select(o => o.Address).ToList();
        if (c.Send && mine.Contains(to, StringComparer.OrdinalIgnoreCase))
        {
            throw AppException.BadRequest("read_receipt_not_requested"); // our own request: nothing to confirm
        }

        // $MDNSent first (RFC 3503): only the one request that sets it sends, so two windows or two clicks send one receipt.
        var claimed = await store.ApplyAsync(access, [m.Id], new MessageChange { AddKeywords = [ReadReceipts.SentKeyword] }, ct);
        if (claimed.Messages == 0)
        {
            return new ReadReceiptResult("ALREADY");
        }

        if (!c.Send)
        {
            return new ReadReceiptResult("DECLINED");
        }

        // As the address the mail came to, when it is one of mine (like an invitation answer).
        var from = m.To.Concat(m.Cc).Select(a => a.Address).FirstOrDefault(a => mine.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? mine[0];
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == access.ActorUserId, ct);
        var bytes = MdnBuilder.Build(new MdnInput(new MailboxAddress(user.DisplayName, from), to, m.Subject ?? "", m.RfcMessageId, m.SentAt,
            m.To.Select(a => a.Address).ToList(), DateTimeOffset.UtcNow, c.Automatic, settings.MailHostname,
            CultureInfo.GetCultureInfo(user.Locale ?? "en"), CalendarLimits.Zone(user.TimeZone)));
        var signed = await mail.SignAsync(access.TenantId, from[(from.LastIndexOf('@') + 1)..], bytes, ct);
        // RFC 8098 2.1 / 3: the MDN goes with a null envelope sender, so nothing (no DSN, no MDN) comes back for it.
        if (!await mail.SendAsync(access.TenantId, access.ActorUserId, "", to, signed, notification: true, user.Locale, null, ct))
        {
            throw AppException.BadRequest("read_receipt_unreachable");
        }

        return new ReadReceiptResult("SENT");
    }
}

public sealed record MdnInput(
    MailboxAddress From,
    string To,
    string OriginalSubject,
    string? OriginalMessageId,
    DateTimeOffset? OriginalSentAt,
    IReadOnlyList<string> OriginalTo,
    DateTimeOffset ReadAt,
    bool Automatic,
    string ReportingHost,
    CultureInfo Culture,
    TimeZoneInfo Zone);

/// <summary>
/// Message Disposition Notification (RFC 8098) in a multipart/report (RFC 6522): a readable part in the reader's
/// language, as Outlook writes it, and the machine-readable message/disposition-notification part.
/// </summary>
public static class MdnBuilder
{
    public static byte[] Build(MdnInput i)
    {
        var culture = i.Culture;
        string Local(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, i.Zone).ToString("g", culture);
        var args = new Dictionary<string, object?>
        {
            ["subject"] = i.OriginalSubject,
            ["to"] = string.Join(", ", i.OriginalTo),
            ["sent"] = i.OriginalSentAt is { } sent ? Local(sent) : "",
            ["read"] = Local(i.ReadAt),
        };
        var text = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("mdn.body", culture), culture, args);

        var report = new StringBuilder();
        report.Append("Reporting-UA: ").Append(i.ReportingHost).Append("; BlueMail\r\n");
        report.Append("Final-Recipient: rfc822;").Append(i.From.Address).Append("\r\n");
        if (i.OriginalMessageId is { } id)
        {
            report.Append("Original-Message-ID: <").Append(id).Append(">\r\n");
        }

        report.Append("Disposition: ").Append(i.Automatic ? "automatic-action/MDN-sent-automatically" : "manual-action/MDN-sent-manually")
            .Append("; displayed\r\n");

        var mediaType = Ascii.IsValid(i.From.Address) ? "message/disposition-notification" : "message/global-disposition-notification"; // RFC 6533
        return new MessageBuilder
        {
            From = i.From,
            To = [new MailboxAddress(null, i.To)],
            Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("mdn.subject", culture), culture, args),
            Date = i.ReadAt,
            MessageId = MessageBuilder.NewMessageId(i.ReportingHost),
            InReplyTo = i.OriginalMessageId,
            References = i.OriginalMessageId is null ? [] : [i.OriginalMessageId],
            TextBody = text,
            ExtraHeaders = i.Automatic ? [("Auto-Submitted", "auto-replied")] : [],
            Report = ("disposition-notification", mediaType, report.ToString()),
        }.ToBytes();
    }
}
