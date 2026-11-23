using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;

namespace BlueMail.Application.Features.Mail;

/// <summary>Opens the caller's mailbox; "not yours" and "does not exist" give the same answer (no probing).</summary>
public sealed class MailboxOpener(IMailStore store, ICurrentUser me)
{
    public async Task<MailboxAccess> OpenAsync(Guid? mailboxId, CancellationToken ct)
    {
        try
        {
            return await store.OpenAsync(me.TenantId, me.UserId, mailboxId, ct);
        }
        catch (MailboxAccessDeniedException)
        {
            throw AppException.NotFound("mailbox_not_found");
        }
    }
}

// ---------- list ----------

public sealed record ListConversationsQuery(Guid? MailboxId, Guid? LabelId, string? View, bool UnreadOnly, bool Threads, string? Cursor, int Limit)
    : IRequest<ConversationPage>;

public sealed class ListConversationsValidator : AbstractValidator<ListConversationsQuery>
{
    public ListConversationsValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, PgMailStore.MaxPageSize);
        RuleFor(x => x.View).Must(v => v is null or "label" or "all").WithErrorCode("invalid_view");
        RuleFor(x => x.LabelId).NotNull().When(x => x.View is null or "label");
        RuleFor(x => x.Cursor).MaximumLength(200);
    }
}

public sealed class ListConversationsHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<ListConversationsQuery, ConversationPage>
{
    public async Task<ConversationPage> Handle(ListConversationsQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var view = q.View == "all" ? MailView.AllMail : MailView.Label;
        return await store.ListConversationsAsync(access, new ConversationQuery(view, q.LabelId, q.UnreadOnly, q.Threads, q.Cursor, q.Limit), ct);
    }
}

// ---------- thread ----------

public sealed record AttachmentDto(Guid Id, string PartPath, string? FileName, string ContentType, long Size, bool Inline, VirusStatus VirusStatus,
    string? DownloadUrl, string? ViewUrl);

public sealed record MessageDto(MessageView Message, IReadOnlyList<AttachmentDto> Attachments, string RawUrl);

public sealed record ThreadDto(Guid ThreadId, string? Subject, bool Muted, IReadOnlyList<MessageDto> Messages);

public sealed record GetThreadQuery(Guid? MailboxId, Guid ThreadId) : IRequest<ThreadDto>;

public sealed class GetThreadHandler(MailboxOpener opener, PgMailStore store, SignedUrls signer, BmSettings settings) : IRequestHandler<GetThreadQuery, ThreadDto>
{
    /// <summary>Types the browser may display inline from our origin (served with nosniff and a sandbox CSP).</summary>
    public static readonly IReadOnlySet<string> Viewable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain" };

    public async Task<ThreadDto> Handle(GetThreadQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var thread = await store.GetThreadAsync(access, q.ThreadId, ct) ?? throw AppException.NotFound("thread_not_found");
        var messages = thread.Messages.Select(m => new MessageDto(m with { Attachments = [] },
            m.Attachments.Select(a => Attachment(access, m.Id, a)).ToList(),
            Url(SignedUrls.Grant(SignedKind.Raw, access.TenantId, access.ActorUserId, access.MailboxId, m.Id, download: true)))).ToList();
        return new ThreadDto(thread.ThreadId, thread.Subject, thread.Muted, messages);
    }

    private AttachmentDto Attachment(MailboxAccess access, Guid messageId, AttachmentInfo a)
    {
        if (a.VirusStatus == VirusStatus.Infected)
        {
            return new AttachmentDto(a.Id, a.PartPath, a.FileName, a.ContentType, a.Size, a.Inline, a.VirusStatus, null, null);
        }

        var download = Url(SignedUrls.Grant(SignedKind.Part, access.TenantId, access.ActorUserId, access.MailboxId, messageId, a.PartPath, download: true));
        var view = Viewable.Contains(a.ContentType)
            ? Url(SignedUrls.Grant(SignedKind.Part, access.TenantId, access.ActorUserId, access.MailboxId, messageId, a.PartPath))
            : null;
        return new AttachmentDto(a.Id, a.PartPath, a.FileName, a.ContentType, a.Size, a.Inline, a.VirusStatus, download, view);
    }

    private string Url(SignedGrant g) => settings.WebUrl + "/api/v1/mail/p/" + signer.Sign(g);
}

// ---------- body ----------

public sealed record MessageBodyDto(string? HtmlDocument, string? Text, bool HasRemoteImages, bool RemoteImagesShown, bool SenderTrusted, string? Calendar,
    string? ZipUrl);

public sealed record GetMessageBodyQuery(Guid? MailboxId, Guid MessageId, bool ShowImages) : IRequest<MessageBodyDto>;

public sealed class GetMessageBodyHandler(MailboxOpener opener, PgMailStore store, SignedUrls signer, BmSettings settings) : IRequestHandler<GetMessageBodyQuery, MessageBodyDto>
{
    public async Task<MessageBodyDto> Handle(GetMessageBodyQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var message = await store.GetMessageAsync(access, q.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        var content = await store.GetContentAsync(access, q.MessageId, ct);
        if (content is null)
        {
            return new MessageBodyDto(null, message.Snippet, false, false, false, null, null);
        }

        var trusted = message.From is { } f && await store.IsImageSenderTrustedAsync(access, f.Address, ct);
        string Sign(SignedGrant g) => signer.Sign(g);
        string? html = null;
        bool hasRemote = false, shown = false;
        if (content.Html is { } raw)
        {
            var infected = message.Attachments.Where(a => a.VirusStatus == VirusStatus.Infected).Select(a => a.PartPath).ToHashSet();
            var clean = MailHtml.Sanitize(raw,
                cid => content.ContentIds.TryGetValue(cid, out var part) && !infected.Contains(part)
                    ? settings.WebUrl + "/api/v1/mail/p/" + Sign(SignedUrls.Grant(SignedKind.Part, access.TenantId, access.ActorUserId, access.MailboxId, message.Id, part))
                    : null,
                url => settings.WebUrl + "/api/v1/mail/img/" + Sign(SignedUrls.Grant(SignedKind.Image, access.TenantId, access.ActorUserId, access.MailboxId, message.Id, url: url)),
                q.ShowImages || trusted);
            html = MailHtml.Document(clean.Html, settings.WebUrl, rtl: false);
            hasRemote = clean.HasRemoteImages;
            shown = clean.RemoteImagesShown;
        }

        var files = message.Attachments.Count(a => !a.Inline && a.VirusStatus != VirusStatus.Infected);
        var zip = files > 1
            ? settings.WebUrl + "/api/v1/mail/p/" + Sign(SignedUrls.Grant(SignedKind.Zip, access.TenantId, access.ActorUserId, access.MailboxId, message.Id, download: true))
            : null;
        return new MessageBodyDto(html, content.Text, hasRemote, shown, trusted, content.Calendar, zip);
    }
}

// ---------- actions ----------

public static class MailActionNames
{
    public const string MarkRead = "markRead", MarkUnread = "markUnread", Star = "star", Unstar = "unstar", Important = "important",
        NotImportant = "notImportant", Archive = "archive", MoveToInbox = "moveToInbox", Trash = "trash", Untrash = "untrash", Spam = "spam",
        NotSpam = "notSpam", AddLabel = "addLabel", RemoveLabel = "removeLabel", MoveTo = "moveTo", DeleteForever = "deleteForever",
        Mute = "mute", Unmute = "unmute", ReportPhishing = "reportPhishing";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MarkRead, MarkUnread, Star, Unstar, Important, NotImportant, Archive, MoveToInbox, Trash, Untrash, Spam, NotSpam, AddLabel, RemoveLabel,
        MoveTo, DeleteForever, Mute, Unmute, ReportPhishing,
    };
}

/// <summary>One action on threads and/or messages. <see cref="FromLabelId"/> is the list the user acts from (for "move").</summary>
public sealed record MailActionCommand(Guid? MailboxId, string Action, IReadOnlyList<Guid> ThreadIds, IReadOnlyList<Guid> MessageIds, Guid? LabelId, Guid? FromLabelId)
    : IRequest<ChangeResult>;

public sealed class MailActionValidator : AbstractValidator<MailActionCommand>
{
    public const int MaxItems = 500; // larger selections run as background jobs (PROMPT 8.3)

    public MailActionValidator()
    {
        RuleFor(x => x.Action).Must(MailActionNames.All.Contains).WithErrorCode("invalid_action");
        RuleFor(x => x.ThreadIds.Count + x.MessageIds.Count).InclusiveBetween(1, MaxItems).WithErrorCode("selection_size");
        RuleFor(x => x.LabelId).NotNull().When(x => x.Action is MailActionNames.AddLabel or MailActionNames.RemoveLabel or MailActionNames.MoveTo);
    }
}

public sealed class MailActionHandler(MailboxOpener opener, PgMailStore store, Antispam.SpamLearning learning, Antispam.PhishingReports phishing)
    : IRequestHandler<MailActionCommand, ChangeResult>
{
    public async Task<ChangeResult> Handle(MailActionCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        if ((access.Rights & MailboxRights.Write) == 0)
        {
            throw AppException.Forbidden("mailbox_read_only");
        }

        if (c.Action is MailActionNames.Mute or MailActionNames.Unmute)
        {
            var threads = c.ThreadIds.ToList();
            if (c.MessageIds.Count > 0)
            {
                threads.AddRange(await store.GetMessagesThreadsAsync(access, c.MessageIds, ct));
            }

            await store.SetThreadMutedAsync(access, threads, c.Action == MailActionNames.Mute, ct);
            return new ChangeResult(threads.Count, 0);
        }

        var ids = await store.ResolveMessagesAsync(access, c.ThreadIds, c.MessageIds, ct);
        var change = await ChangeFor(access, c, ct);
        try
        {
            var result = await store.ApplyAsync(access, ids, change, ct);
            // "Report spam" / "Not spam" / "Report phishing" teach the user's and the tenant's Bayes models (PROMPT 6.9).
            if (c.Action is MailActionNames.Spam or MailActionNames.ReportPhishing or MailActionNames.NotSpam)
            {
                await learning.LearnAsync(access, ids, spam: c.Action != MailActionNames.NotSpam, ct);
            }

            if (c.Action == MailActionNames.ReportPhishing)
            {
                await phishing.ForwardAsync(access, ids, ct);
            }

            return result;
        }
        catch (LabelNotFoundException)
        {
            throw AppException.NotFound("label_not_found");
        }
    }

    private async Task<MessageChange> ChangeFor(MailboxAccess access, MailActionCommand c, CancellationToken ct)
    {
        IReadOnlyList<Guid> Label() => c.LabelId is { } l ? [l] : [];
        switch (c.Action)
        {
            case MailActionNames.MarkRead: return new MessageChange { Seen = true };
            case MailActionNames.MarkUnread: return new MessageChange { Seen = false };
            case MailActionNames.Star: return new MessageChange { Starred = true };
            case MailActionNames.Unstar: return new MessageChange { Starred = false };
            case MailActionNames.Important: return new MessageChange { Important = true };
            case MailActionNames.NotImportant: return new MessageChange { Important = false };
            case MailActionNames.Archive: return new MessageChange { RemoveSystem = [SystemLabels.Inbox] };
            case MailActionNames.MoveToInbox: return new MessageChange { AddSystem = [SystemLabels.Inbox], RemoveSystem = [SystemLabels.Trash, SystemLabels.Spam] };
            case MailActionNames.Trash: return new MessageChange { AddSystem = [SystemLabels.Trash], RemoveSystem = [SystemLabels.Inbox, SystemLabels.Spam] };
            case MailActionNames.Untrash: return new MessageChange { AddSystem = [SystemLabels.Inbox], RemoveSystem = [SystemLabels.Trash] };
            case MailActionNames.Spam: return new MessageChange { AddSystem = [SystemLabels.Spam], RemoveSystem = [SystemLabels.Inbox, SystemLabels.Trash] };
            case MailActionNames.NotSpam: return new MessageChange { AddSystem = [SystemLabels.Inbox], RemoveSystem = [SystemLabels.Spam] };
            case MailActionNames.ReportPhishing: return new MessageChange { AddSystem = [SystemLabels.Spam], RemoveSystem = [SystemLabels.Inbox, SystemLabels.Trash] };
            case MailActionNames.AddLabel: return new MessageChange { AddLabels = Label() };
            case MailActionNames.RemoveLabel: return new MessageChange { RemoveLabels = Label() };
            case MailActionNames.MoveTo:
                // Gmail "Move to": add the target, remove the list the user is in (Inbox when unknown).
                var remove = c.FromLabelId is { } from ? new List<Guid> { from } : [];
                return new MessageChange
                {
                    AddLabels = Label(), RemoveLabels = remove,
                    RemoveSystem = c.FromLabelId is null ? [SystemLabels.Inbox] : [],
                };
            case MailActionNames.DeleteForever:
                // Only from Trash or Spam (PROMPT 6.2: no accidental permanent delete from other lists).
                var allowed = await store.InTrashOrSpamAsync(access, await store.ResolveMessagesAsync(access, c.ThreadIds, c.MessageIds, ct), ct);
                if (!allowed)
                {
                    throw AppException.BadRequest("delete_forever_needs_trash");
                }

                return new MessageChange { Expunge = true };
            default:
                throw AppException.BadRequest("invalid_action");
        }
    }
}

// ---------- changes, labels, trust, original ----------

public sealed record GetChangesQuery(Guid? MailboxId, long SinceModseq) : IRequest<MailChanges>;

public sealed class GetChangesHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<GetChangesQuery, MailChanges>
{
    public async Task<MailChanges> Handle(GetChangesQuery q, CancellationToken ct) =>
        await store.GetChangesAsync(await opener.OpenAsync(q.MailboxId, ct), Math.Max(0, q.SinceModseq), ct);
}

public sealed record SaveLabelCommand(Guid? MailboxId, Guid? LabelId, string Name, Guid? ParentId, string? ColorBg, string? ColorText) : IRequest<Guid>;

public sealed class SaveLabelValidator : AbstractValidator<SaveLabelCommand>
{
    public SaveLabelValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(225).Must(n => !n.Contains('/', StringComparison.Ordinal)).WithErrorCode("label_name_slash");
        RuleFor(x => x.ColorBg).Matches("^#[0-9a-fA-F]{6}$").When(x => x.ColorBg is not null);
        RuleFor(x => x.ColorText).Matches("^#[0-9a-fA-F]{6}$").When(x => x.ColorText is not null);
    }
}

public sealed class SaveLabelHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<SaveLabelCommand, Guid>
{
    public async Task<Guid> Handle(SaveLabelCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        try
        {
            if (c.LabelId is { } id)
            {
                await store.UpdateLabelAsync(access, id, c.Name, c.ColorBg, c.ColorText, ct);
                return id;
            }

            return await store.CreateLabelAsync(access, c.Name, c.ParentId, c.ColorBg, c.ColorText, ct);
        }
        catch (LabelNotFoundException)
        {
            throw AppException.NotFound("label_not_found");
        }
        catch (LabelConflictException)
        {
            throw AppException.Conflict("label_exists");
        }
    }
}

public sealed record DeleteLabelCommand(Guid? MailboxId, Guid LabelId) : IRequest<Unit>;

public sealed class DeleteLabelHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<DeleteLabelCommand, Unit>
{
    public async Task<Unit> Handle(DeleteLabelCommand c, CancellationToken ct)
    {
        try
        {
            await store.DeleteLabelAsync(await opener.OpenAsync(c.MailboxId, ct), c.LabelId, ct);
            return Unit.Value;
        }
        catch (LabelNotFoundException)
        {
            throw AppException.NotFound("label_not_found");
        }
    }
}

public sealed record TrustImageSenderCommand(Guid? MailboxId, Guid MessageId) : IRequest<Unit>;

public sealed class TrustImageSenderHandler(MailboxOpener opener, PgMailStore store) : IRequestHandler<TrustImageSenderCommand, Unit>
{
    public async Task<Unit> Handle(TrustImageSenderCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        var m = await store.GetMessageAsync(access, c.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        if (m.From is { } f)
        {
            await store.TrustImageSenderAsync(access, f.Address, ct);
        }

        return Unit.Value;
    }
}

public sealed record OriginalDto(string Headers, string? AuthResults, long Size, string RawUrl);

public sealed record GetOriginalQuery(Guid? MailboxId, Guid MessageId) : IRequest<OriginalDto>;

public sealed class GetOriginalHandler(MailboxOpener opener, PgMailStore store, SignedUrls signer, BmSettings settings) : IRequestHandler<GetOriginalQuery, OriginalDto>
{
    public const int MaxShownBytes = 512 * 1024;

    public async Task<OriginalDto> Handle(GetOriginalQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        var m = await store.GetMessageAsync(access, q.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        using var raw = new MemoryStream();
        await store.CopyRawAsync(access, q.MessageId, raw, ct);
        var bytes = raw.ToArray();
        var shown = bytes.AsSpan(0, Math.Min(bytes.Length, MaxShownBytes));
        var text = System.Text.Unicode.Utf8.IsValid(shown) ? System.Text.Encoding.UTF8.GetString(shown) : System.Text.Encoding.Latin1.GetString(shown);
        var url = settings.WebUrl + "/api/v1/mail/p/" + signer.Sign(SignedUrls.Grant(SignedKind.Raw, access.TenantId, access.ActorUserId, access.MailboxId, m.Id, download: true));
        return new OriginalDto(text, m.AuthResults, m.Size, url);
    }
}
