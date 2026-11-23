using System.Net;
using System.Text;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Sending;

internal static class ComposeErrors
{
    /// <summary>Maps compose problems to API errors with stable codes.</summary>
    public static async Task<T> Guard<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (ComposeException e)
        {
            var code = e.Code.Split(':')[0];
            throw code switch
            {
                "draft_not_found" => AppException.NotFound(code),
                "draft_changed" or "already_sending" or "draft_sending" => AppException.Conflict(code),
                "from_not_allowed" => AppException.Forbidden(code),
                _ => AppException.BadRequest(code, e.Code.Contains(':', StringComparison.Ordinal)
                    ? new Dictionary<string, object?> { ["address"] = e.Code[(e.Code.IndexOf(':') + 1)..] }
                    : null),
            };
        }
    }
}

public sealed record FromOptionsQuery(Guid? MailboxId) : IRequest<IReadOnlyList<SenderPolicy.FromOption>>;

public sealed class FromOptionsHandler(MailboxOpener opener, SenderPolicy senders) : IRequestHandler<FromOptionsQuery, IReadOnlyList<SenderPolicy.FromOption>>
{
    public async Task<IReadOnlyList<SenderPolicy.FromOption>> Handle(FromOptionsQuery q, CancellationToken ct) =>
        await senders.OptionsAsync(await opener.OpenAsync(q.MailboxId, ct), ct);
}

// ---------- drafts ----------

public sealed record SaveDraftCommand(Guid? MailboxId, Guid? DraftId, int? Version, DraftState State) : IRequest<DraftService.DraftDto>;

public sealed class SaveDraftValidator : AbstractValidator<SaveDraftCommand>
{
    public SaveDraftValidator()
    {
        RuleFor(x => x.State.Subject).MaximumLength(998).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.State.Html.Length).LessThanOrEqualTo(5_000_000).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.State.To.Count + x.State.Cc.Count + x.State.Bcc.Count).LessThanOrEqualTo(SendService.MaxRecipients).WithErrorCode(ValidationCodes.TooMany);
        RuleFor(x => x.State.Attachments.Count).LessThanOrEqualTo(100).WithErrorCode(ValidationCodes.TooMany);
    }
}

public sealed class SaveDraftHandler(MailboxOpener opener, DraftService drafts) : IRequestHandler<SaveDraftCommand, DraftService.DraftDto>
{
    public async Task<DraftService.DraftDto> Handle(SaveDraftCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        return await ComposeErrors.Guard(() => drafts.SaveAsync(access, c.DraftId, c.Version, c.State, ct));
    }
}

public sealed record GetDraftQuery(Guid? MailboxId, Guid? DraftId, Guid? MessageId) : IRequest<DraftService.DraftDto>;

public sealed class GetDraftHandler(MailboxOpener opener, DraftService drafts) : IRequestHandler<GetDraftQuery, DraftService.DraftDto>
{
    public async Task<DraftService.DraftDto> Handle(GetDraftQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var id = q.DraftId ?? (q.MessageId is { } m ? await drafts.FindByMessageAsync(access, m, ct) : null);
        return id is { } d && await drafts.GetAsync(access, d, ct) is { } dto ? dto : throw AppException.NotFound("draft_not_found");
    }
}

public sealed record DiscardDraftCommand(Guid? MailboxId, Guid DraftId) : IRequest<Unit>;

public sealed class DiscardDraftHandler(MailboxOpener opener, DraftService drafts) : IRequestHandler<DiscardDraftCommand, Unit>
{
    public async Task<Unit> Handle(DiscardDraftCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        return await ComposeErrors.Guard(async () =>
        {
            await drafts.DiscardAsync(access, c.DraftId, ct);
            return Unit.Value;
        });
    }
}

// ---------- send ----------

public sealed record SendDraftCommand(Guid? MailboxId, Guid DraftId, DateTimeOffset? ScheduleAt, bool Archive, int UndoSeconds) : IRequest<SendService.SendResult>;

public sealed class SendDraftValidator : AbstractValidator<SendDraftCommand>
{
    public SendDraftValidator() =>
        RuleFor(x => x.ScheduleAt).Must(t => t is null || t < DateTimeOffset.UtcNow.AddYears(2)).WithErrorCode("schedule_too_far");
}

public sealed class SendDraftHandler(MailboxOpener opener, SendService sends) : IRequestHandler<SendDraftCommand, SendService.SendResult>
{
    public async Task<SendService.SendResult> Handle(SendDraftCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        return await ComposeErrors.Guard(() => sends.SendAsync(access, c.DraftId, c.ScheduleAt, c.Archive, c.UndoSeconds, ct));
    }
}

public sealed record CancelSendCommand(Guid? MailboxId, Guid OutgoingId) : IRequest<Guid>;

public sealed class CancelSendHandler(MailboxOpener opener, SendService sends) : IRequestHandler<CancelSendCommand, Guid>
{
    public async Task<Guid> Handle(CancelSendCommand c, CancellationToken ct) =>
        await sends.CancelAsync(await opener.OpenAsync(c.MailboxId, ct), c.OutgoingId, ct) ?? throw AppException.Conflict("too_late_to_undo");
}

public sealed record OutgoingDto(Guid Id, Guid DraftId, string Status, DateTimeOffset SendAt);

public sealed record PendingSendsQuery(Guid? MailboxId) : IRequest<IReadOnlyList<OutgoingDto>>;

public sealed class PendingSendsHandler(MailboxOpener opener, SendService sends) : IRequestHandler<PendingSendsQuery, IReadOnlyList<OutgoingDto>>
{
    public async Task<IReadOnlyList<OutgoingDto>> Handle(PendingSendsQuery q, CancellationToken ct) =>
        (await sends.PendingAsync(await opener.OpenAsync(q.MailboxId, ct), ct))
        .Select(o => new OutgoingDto(o.Id, o.DraftId, Infrastructure.Persistence.EnumNames.ToUpperSnake(o.Status.ToString()), o.SendAt)).ToList();
}

// ---------- reply / forward prefill ----------

public sealed record ComposeFromMessageQuery(Guid? MailboxId, Guid MessageId, string Mode) : IRequest<DraftState>;

public sealed class ComposeFromMessageValidator : AbstractValidator<ComposeFromMessageQuery>
{
    public ComposeFromMessageValidator() => RuleFor(x => x.Mode).Must(m => m is "reply" or "replyAll" or "forward").WithErrorCode("invalid_mode");
}

/// <summary>
/// Reply / reply all / forward (PROMPT 6.4): In-Reply-To and References chained, "Re:"/"Fwd:" added once, the
/// original quoted (blockquote with attribution), recipients chosen per RFC 5322 3.6.3 (Reply-To wins over From;
/// reply all adds To/Cc minus the replier's own addresses). Forward carries the original files as attachments.
/// </summary>
public sealed class ComposeFromMessageHandler(MailboxOpener opener, PgMailStore store, SenderPolicy senders) : IRequestHandler<ComposeFromMessageQuery, DraftState>
{
    public async Task<DraftState> Handle(ComposeFromMessageQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var m = await store.GetMessageAsync(access, q.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        var content = await store.GetContentAsync(access, q.MessageId, ct);
        var mine = (await senders.OptionsAsync(access, ct)).Select(o => o.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Reply from the address the message was sent to, when it is one of ours.
        var from = m.To.Concat(m.Cc).Select(a => a.Address).FirstOrDefault(mine.Contains) ?? mine.First();

        var subject = m.Subject ?? "";
        var quoted = Quote(m, content);
        var refs = (m.RfcMessageId is null ? [] : new List<string> { m.RfcMessageId }).ToList();
        if (q.Mode == "forward")
        {
            return new DraftState
            {
                From = from,
                Subject = Prefix("Fwd:", subject, ["fwd", "fw", "tr", "wg", "rv", "enc", "ct", "chuyển tiếp"]),
                Html = "<p></p>" + quoted,
                InReplyTo = null,
                References = refs,
                ReplyToMessageId = m.Id,
                Mode = "forward",
                Attachments = m.Attachments.Where(a => !a.Inline && a.VirusStatus != VirusStatus.Infected)
                    .Select(a => new DraftAttachment("part", null, m.Id, a.PartPath, a.FileName ?? "attachment", a.ContentType, a.Size)).ToList(),
            };
        }

        var replyTargets = m.ReplyTo.Count > 0 ? m.ReplyTo : m.From is { } f ? [f] : [];
        // Replying to a message I sent goes to its original recipients (like Gmail).
        if (m.From is { } sender && mine.Contains(sender.Address))
        {
            replyTargets = m.To;
        }

        var to = replyTargets.Select(a => new DraftAddress(a.Name, a.Address)).ToList();
        var cc = new List<DraftAddress>();
        if (q.Mode == "replyAll")
        {
            var seen = new HashSet<string>(to.Select(a => a.Address), StringComparer.OrdinalIgnoreCase);
            foreach (var a in m.To.Concat(m.Cc))
            {
                if (!mine.Contains(a.Address) && seen.Add(a.Address))
                {
                    cc.Add(new DraftAddress(a.Name, a.Address));
                }
            }
        }

        return new DraftState
        {
            From = from,
            To = to,
            Cc = cc,
            Subject = Prefix("Re:", subject, ["re", "aw", "sv", "vs", "antw", "odp", "res", "tl", "trả lời"]),
            Html = "<p></p>" + quoted,
            InReplyTo = m.RfcMessageId,
            References = await ReferencesAsync(access, m, ct),
            ReplyToMessageId = m.Id,
            Mode = q.Mode,
        };
    }

    private async Task<IReadOnlyList<string>> ReferencesAsync(MailboxAccess access, MessageView m, CancellationToken ct)
    {
        // RFC 5322 3.6.4: parent's References + parent's Message-ID (kept to a sane length).
        var thread = await store.GetThreadAsync(access, m.ThreadId, ct);
        var ids = thread?.Messages.Where(x => x.ReceivedAt <= m.ReceivedAt).Select(x => x.RfcMessageId).Where(x => x is not null).Select(x => x!).ToList() ?? [];
        if (m.RfcMessageId is not null && !ids.Contains(m.RfcMessageId))
        {
            ids.Add(m.RfcMessageId);
        }

        return ids.Count > 20 ? [ids[0], .. ids.TakeLast(19)] : ids;
    }

    public static string Prefix(string prefix, string subject, string[] known)
    {
        var s = subject.TrimStart();
        var colon = s.IndexOfAny([':', '：']);
        if (colon > 0 && colon <= 15 && known.Contains(s[..colon].Trim().ToLowerInvariant()))
        {
            return s;
        }

        return prefix + " " + s;
    }

    private static string Quote(MessageView m, MessageContent? content)
    {
        var who = m.From is null ? "" : WebUtility.HtmlEncode(m.From.Name is { Length: > 0 } n ? $"{n} <{m.From.Address}>" : m.From.Address);
        var when = (m.SentAt ?? m.ReceivedAt).ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        var body = content?.Html is { } html
            ? MailHtml.Sanitize(html, _ => null, u => u, showRemote: false).Html
            : "<pre>" + WebUtility.HtmlEncode(content?.Text ?? m.Snippet ?? "") + "</pre>";
        var sb = new StringBuilder();
        sb.Append("<div class=\"bm-quote\"><p>").Append(WebUtility.HtmlEncode(when)).Append(", ").Append(who).Append(":</p>");
        sb.Append("<blockquote style=\"margin:0 0 0 .8ex;border-inline-start:1px solid #ccc;padding-inline-start:1ex\">").Append(body).Append("</blockquote></div>");
        return sb.ToString();
    }
}

// ---------- recipient suggestions ----------

public sealed record SuggestedAddress(string? Name, string Address, string Kind);

public sealed record SuggestRecipientsQuery(Guid? MailboxId, string Q) : IRequest<IReadOnlyList<SuggestedAddress>>;

/// <summary>Suggestions from the organisation (users, groups, shared mailboxes) and from people this mailbox wrote with.</summary>
public sealed class SuggestRecipientsHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<SuggestRecipientsQuery, IReadOnlyList<SuggestedAddress>>
{
    public async Task<IReadOnlyList<SuggestedAddress>> Handle(SuggestRecipientsQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var term = q.Q.Trim().ToLowerInvariant();
        if (term.Length == 0 || term.Length > 100)
        {
            return [];
        }

        var like = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var users = await db.Users.AsNoTracking().Where(u => u.TenantId == access.TenantId && u.DeletedAt == null &&
                (EF.Functions.ILike(u.PrimaryEmail, like) || EF.Functions.ILike(EF.Functions.Unaccent(u.DisplayName), EF.Functions.Unaccent(like))))
            .OrderBy(u => u.DisplayName).Take(8).Select(u => new SuggestedAddress(u.DisplayName, u.PrimaryEmail, "user")).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => g.TenantId == access.TenantId && g.DeletedAt == null &&
                (EF.Functions.ILike(g.Email, like) || EF.Functions.ILike(g.Name, like)))
            .Take(4).Select(g => new SuggestedAddress(g.Name, g.Email, "group")).ToListAsync(ct);
        var shared = await db.SharedMailboxes.AsNoTracking().Where(s => s.TenantId == access.TenantId && s.DeletedAt == null &&
                (EF.Functions.ILike(s.Email, like) || EF.Functions.ILike(s.Name, like)))
            .Take(4).Select(s => new SuggestedAddress(s.Name, s.Email, "shared")).ToListAsync(ct);
        var people = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && m.FromAddr != null && m.ExpungedAt == null &&
                        (EF.Functions.ILike(m.FromAddr!, like) || EF.Functions.ILike(m.FromName ?? "", like)))
            .GroupBy(m => m.FromAddr!)
            .OrderByDescending(g => g.Max(m => m.ReceivedAt)).Take(8)
            .Select(g => new SuggestedAddress(g.Max(m => m.FromName), g.Key, "recent")).ToListAsync(ct);
        return users.Concat(groups).Concat(shared).Concat(people)
            .DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }
}

// ---------- signatures & templates ----------

public sealed record SignatureDto(Guid Id, string Name, string Html, bool IsDefaultNew, bool IsDefaultReply, string? FromAddress);

public sealed record ListSignaturesQuery : IRequest<IReadOnlyList<SignatureDto>>;

public sealed class ListSignaturesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListSignaturesQuery, IReadOnlyList<SignatureDto>>
{
    public async Task<IReadOnlyList<SignatureDto>> Handle(ListSignaturesQuery q, CancellationToken ct) =>
        await db.Signatures.AsNoTracking().Where(s => s.UserId == me.UserId).OrderBy(s => s.Name)
            .Select(s => new SignatureDto(s.Id, s.Name, s.Html, s.IsDefaultNew, s.IsDefaultReply, s.FromAddress)).ToListAsync(ct);
}

public sealed record SaveSignatureCommand(Guid? Id, string Name, string Html, bool IsDefaultNew, bool IsDefaultReply, string? FromAddress) : IRequest<Guid>;

public sealed class SaveSignatureValidator : AbstractValidator<SaveSignatureCommand>
{
    public SaveSignatureValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode(ValidationCodes.Required).MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Html.Length).LessThanOrEqualTo(100_000).WithErrorCode(ValidationCodes.TooLong);
    }
}

public sealed class SaveSignatureHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveSignatureCommand, Guid>
{
    public async Task<Guid> Handle(SaveSignatureCommand c, CancellationToken ct)
    {
        var s = c.Id is { } id
            ? await db.Signatures.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("signature_not_found")
            : db.Signatures.Add(new Signature { TenantId = me.TenantId, UserId = me.UserId }).Entity;
        s.Name = c.Name.Trim();
        // Stored sanitized: signatures are inserted into outgoing HTML and shown in the editor.
        s.Html = MailHtml.Sanitize(c.Html, _ => null, u => u, showRemote: true).Html;
        s.IsDefaultNew = c.IsDefaultNew;
        s.IsDefaultReply = c.IsDefaultReply;
        s.FromAddress = c.FromAddress?.ToLowerInvariant();
        if (c.IsDefaultNew || c.IsDefaultReply)
        {
            foreach (var other in await db.Signatures.Where(x => x.UserId == me.UserId && x.Id != s.Id && x.FromAddress == s.FromAddress).ToListAsync(ct))
            {
                other.IsDefaultNew &= !c.IsDefaultNew;
                other.IsDefaultReply &= !c.IsDefaultReply;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("signature_exists");
        }

        return s.Id;
    }
}

public sealed record DeleteSignatureCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteSignatureHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteSignatureCommand, Unit>
{
    public async Task<Unit> Handle(DeleteSignatureCommand c, CancellationToken ct)
    {
        var n = await db.Signatures.Where(x => x.Id == c.Id && x.UserId == me.UserId).ExecuteUpdateAsync(x => x.SetProperty(y => y.DeletedAt, DateTimeOffset.UtcNow), ct);
        return n == 0 ? throw AppException.NotFound("signature_not_found") : Unit.Value;
    }
}

public sealed record TemplateDto(Guid Id, string Name, string Subject, string Html);

public sealed record ListTemplatesQuery : IRequest<IReadOnlyList<TemplateDto>>;

public sealed class ListTemplatesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListTemplatesQuery, IReadOnlyList<TemplateDto>>
{
    public async Task<IReadOnlyList<TemplateDto>> Handle(ListTemplatesQuery q, CancellationToken ct) =>
        await db.Templates.AsNoTracking().Where(t => t.UserId == me.UserId).OrderBy(t => t.Name)
            .Select(t => new TemplateDto(t.Id, t.Name, t.Subject, t.Html)).ToListAsync(ct);
}

public sealed record SaveTemplateCommand(Guid? Id, string Name, string Subject, string Html) : IRequest<Guid>;

public sealed class SaveTemplateValidator : AbstractValidator<SaveTemplateCommand>
{
    public SaveTemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode(ValidationCodes.Required).MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Subject).MaximumLength(998).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Html.Length).LessThanOrEqualTo(2_000_000).WithErrorCode(ValidationCodes.TooLong);
    }
}

public sealed class SaveTemplateHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveTemplateCommand, Guid>
{
    public async Task<Guid> Handle(SaveTemplateCommand c, CancellationToken ct)
    {
        var t = c.Id is { } id
            ? await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("template_not_found")
            : db.Templates.Add(new MailTemplate { TenantId = me.TenantId, UserId = me.UserId }).Entity;
        t.Name = c.Name.Trim();
        t.Subject = c.Subject;
        t.Html = MailHtml.Sanitize(c.Html, _ => null, u => u, showRemote: true).Html;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("template_exists");
        }

        return t.Id;
    }
}

public sealed record DeleteTemplateCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteTemplateHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteTemplateCommand, Unit>
{
    public async Task<Unit> Handle(DeleteTemplateCommand c, CancellationToken ct)
    {
        var n = await db.Templates.Where(x => x.Id == c.Id && x.UserId == me.UserId).ExecuteUpdateAsync(x => x.SetProperty(y => y.DeletedAt, DateTimeOffset.UtcNow), ct);
        return n == 0 ? throw AppException.NotFound("template_not_found") : Unit.Value;
    }
}
