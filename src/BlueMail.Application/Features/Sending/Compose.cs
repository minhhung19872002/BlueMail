using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Sending;

public sealed record DraftAddress(string? Name, string Address);

/// <summary>An attachment of a draft: an upload, or a part of an existing message (forwarding).</summary>
public sealed record DraftAttachment(string Kind, Guid? UploadId, Guid? MessageId, string? PartPath, string FileName, string ContentType, long Size,
    bool Inline = false);

/// <summary>Everything the compose window holds (saved as JSON in mail.drafts.state).</summary>
public sealed record DraftState
{
    public string? From { get; init; }
    public IReadOnlyList<DraftAddress> To { get; init; } = [];
    public IReadOnlyList<DraftAddress> Cc { get; init; } = [];
    public IReadOnlyList<DraftAddress> Bcc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Html { get; init; } = "";
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public Guid? ReplyToMessageId { get; init; }
    public string Mode { get; init; } = "new";
    public IReadOnlyList<DraftAttachment> Attachments { get; init; } = [];

    /// <summary>Confidential mode (Phase 12): outside recipients get a link instead of the message.</summary>
    public DraftConfidential? Confidential { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IEnumerable<DraftAddress> AllRecipients => To.Concat(Cc).Concat(Bcc);
}

public sealed class ComposeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Turns a draft into RFC 5322 bytes with <see cref="MessageBuilder"/> (PROMPT 6.4): multipart/alternative
/// (text from the HTML + HTML), multipart/related for inline images (cid:{upload}@upload in the editor HTML),
/// multipart/mixed for files; RFC 2047/2231 encodings; Message-ID from the configured host name.
/// Files above the link threshold become download links instead of attachments.
/// </summary>
public sealed partial class MimeComposer(UploadStore uploads, PgMailStore store, BlueMailDbContext db, BmSettings settings)
{
    public const long LinkThresholdBytes = 25L * 1024 * 1024;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    public static MailboxAddress Addr(DraftAddress a) => new(string.IsNullOrWhiteSpace(a.Name) ? null : a.Name.Trim(), a.Address.Trim());

    public async Task<byte[]> BuildAsync(MailboxAccess access, DraftState s, string messageId, DateTimeOffset date, bool includeBcc, bool makeLinks,
        CancellationToken ct)
    {
        var html = s.Html;
        var atts = new List<BuilderAttachment>();
        var links = new List<(string Name, long Size, string Url)>();
        foreach (var a in s.Attachments)
        {
            byte[]? content = null;
            if (a.Kind == "upload" && a.UploadId is { } uid)
            {
                var up = await db.Uploads.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == uid && u.TenantId == access.TenantId && u.UserId == access.ActorUserId && u.DeletedAt == null, ct)
                    ?? throw new ComposeException("attachment_missing");
                if (up.Offset != up.Length)
                {
                    throw new ComposeException("upload_incomplete");
                }

                if (up.ScanStatus == nameof(VirusStatus.Infected))
                {
                    throw new ComposeException("attachment_infected");
                }

                if (makeLinks && !a.Inline && up.Length > LinkThresholdBytes)
                {
                    links.Add((a.FileName, up.Length, await CreateLinkAsync(access, up, ct)));
                    continue;
                }

                content = await uploads.ReadAllAsync(access.TenantId, uid, up.Chunks, ct);
            }
            else if (a.Kind == "part" && a.MessageId is { } mid && a.PartPath is { } path)
            {
                var part = await store.GetPartAsync(access, mid, path, ct) ?? throw new ComposeException("attachment_missing");
                if (part.Info.VirusStatus == VirusStatus.Infected)
                {
                    throw new ComposeException("attachment_infected");
                }

                content = part.Content;
            }

            if (content is null)
            {
                continue;
            }

            var bytes = content;
            atts.Add(new BuilderAttachment
            {
                FileName = a.FileName,
                ContentType = a.ContentType,
                Open = () => new MemoryStream(bytes, writable: false),
                ContentId = a.Inline && a.UploadId is { } u ? $"{u:N}@upload" : null,
            });
        }

        if (links.Count > 0)
        {
            var sb = new StringBuilder(html).Append("<div style=\"margin-top:16px\">");
            foreach (var (name, size, url) in links)
            {
                sb.Append("<p>📎 <a href=\"").Append(System.Net.WebUtility.HtmlEncode(url)).Append("\">").Append(System.Net.WebUtility.HtmlEncode(name))
                  .Append("</a> (").Append(Math.Round(size / 1048576.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" MB)</p>");
            }

            html = sb.Append("</div>").ToString();
        }

        // The editor shows inline images from blob: URLs; the stored HTML references them as cid:{upload}@upload.
        var text = HtmlToText.Convert(html);
        var builder = new MessageBuilder
        {
            From = Addr(new DraftAddress(null, s.From ?? "")) with { Name = await DisplayNameAsync(access, s.From, ct) },
            To = s.To.Select(Addr).ToList(),
            Cc = s.Cc.Select(Addr).ToList(),
            Bcc = s.Bcc.Select(Addr).ToList(),
            IncludeBccHeader = includeBcc,
            Subject = s.Subject,
            Date = date,
            MessageId = messageId,
            InReplyTo = s.InReplyTo,
            References = s.References,
            TextBody = text,
            HtmlBody = html.Length == 0 ? null : html,
            Attachments = atts,
            ExtraHeaders = [("MIME-Version", "1.0")],
        };
        return builder.ToBytes();
    }

    private async Task<string?> DisplayNameAsync(MailboxAccess access, string? from, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == access.ActorUserId).Select(u => new { u.DisplayName, u.PrimaryEmail }).FirstAsync(ct);
        if (from is not null && !from.Equals(user.PrimaryEmail, StringComparison.OrdinalIgnoreCase))
        {
            var shared = await db.SharedMailboxes.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == access.TenantId && m.Email == from.ToLowerInvariant()).Select(m => m.Name).FirstOrDefaultAsync(ct);
            if (shared is not null)
            {
                return shared;
            }
        }

        return user.DisplayName;
    }

    private async Task<string> CreateLinkAsync(MailboxAccess access, Upload up, CancellationToken ct)
    {
        var token = Base64UrlToken();
        db.LargeAttachments.Add(new LargeAttachment
        {
            TenantId = access.TenantId,
            UserId = access.ActorUserId,
            ObjectKey = UploadStore.ChunkKey(access.TenantId, up.Id, 0)[..^7],
            FileName = up.FileName,
            ContentType = up.ContentType,
            Size = up.Length,
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token))),
            ExpiresAt = DateTimeOffset.UtcNow.Add(LinkLifetime),
        });
        // The upload must outlive the link.
        await db.Uploads.IgnoreQueryFilters().Where(u => u.Id == up.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.Add(LinkLifetime).AddDays(1)), ct);
        await db.SaveChangesAsync(ct);
        return settings.WebUrl + "/api/v1/files/" + token;
    }

    private static string Base64UrlToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    /// <summary>cid: references to uploads used in the HTML (inline images).</summary>
    public static IEnumerable<Guid> InlineUploads(string html) =>
        CidUpload().Matches(html).Select(m => Guid.TryParseExact(m.Groups[1].Value, "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct();

    [GeneratedRegex("cid:([0-9a-f]{32})@upload", RegexOptions.IgnoreCase)]
    private static partial Regex CidUpload();
}

/// <summary>
/// Drafts (PROMPT 6.4): the compose state is kept in mail.drafts and every save stores a fresh RFC 5322 version
/// in the Drafts label (the previous version is expunged), so the draft is a real message for IMAP clients.
/// </summary>
public sealed class DraftService(BlueMailDbContext db, PgMailStore store, MimeComposer composer, SenderPolicy senders, BmSettings settings)
{
    public sealed record DraftDto(Guid Id, int Version, DraftState State, Guid? MessageId, DateTimeOffset UpdatedAt);

    public async Task<DraftDto> SaveAsync(MailboxAccess access, Guid? draftId, int? expectedVersion, DraftState state, CancellationToken ct)
    {
        access.Require(MailboxRights.Write);
        state = state with { From = state.From ?? await senders.DefaultFromAsync(access, ct) };
        Draft draft;
        if (draftId is { } id)
        {
            draft = await db.Drafts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == access.TenantId && d.MailboxId == access.MailboxId && d.UserId == access.ActorUserId && d.DeletedAt == null, ct)
                ?? throw new ComposeException("draft_not_found");
            if (expectedVersion is { } v && v != draft.Version)
            {
                throw new ComposeException("draft_changed");
            }

            if (await db.Outgoing.IgnoreQueryFilters().AnyAsync(o => o.DraftId == id && (o.Status == OutgoingStatus.UndoWindow || o.Status == OutgoingStatus.Scheduled || o.Status == OutgoingStatus.Submitting), ct))
            {
                throw new ComposeException("draft_sending");
            }
        }
        else
        {
            draft = new Draft { TenantId = access.TenantId, MailboxId = access.MailboxId, UserId = access.ActorUserId };
            db.Drafts.Add(draft);
        }

        var bytes = await composer.BuildAsync(access, state, MessageBuilder.NewMessageId(settings.MailHostname), DateTimeOffset.UtcNow, includeBcc: true, makeLinks: false, ct);
        var stream = new MemoryStream(bytes, writable: false);
        var facts = MessageFacts.Extract(MimeMessage.Parse(stream), stream);
        stream.Position = 0;
        var delivered = await store.DeliverAsync(new DeliveryRequest(access.TenantId, stream, facts,
            [new MailboxDelivery(access.MailboxId, [SystemLabels.Drafts], Seen: true, Draft: true)], DateTimeOffset.UtcNow), ct);

        var old = draft.MessageId;
        draft.MessageId = delivered[0].MessageId;
        draft.State = JsonSerializer.Serialize(state, DraftState.Json);
        draft.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await store.ApplyAsync(access, [delivered[0].MessageId], new MessageChange { Expunge = true }, ct);
            throw new ComposeException("draft_changed");
        }

        if (old is { } o)
        {
            await store.ApplyAsync(access, [o], new MessageChange { Expunge = true }, ct);
        }

        return new DraftDto(draft.Id, draft.Version, state, draft.MessageId, draft.UpdatedAt);
    }

    public async Task<DraftDto?> GetAsync(MailboxAccess access, Guid draftId, CancellationToken ct)
    {
        var d = await db.Drafts.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == draftId && x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.UserId == access.ActorUserId && x.DeletedAt == null, ct);
        return d is null ? null : new DraftDto(d.Id, d.Version, JsonSerializer.Deserialize<DraftState>(d.State, DraftState.Json)!, d.MessageId, d.UpdatedAt);
    }

    /// <summary>The draft whose current version is this message (opening a draft from the Drafts list).</summary>
    public async Task<Guid?> FindByMessageAsync(MailboxAccess access, Guid messageId, CancellationToken ct) =>
        await db.Drafts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.MessageId == messageId && x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.DeletedAt == null)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public async Task DiscardAsync(MailboxAccess access, Guid draftId, CancellationToken ct)
    {
        var d = await db.Drafts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == draftId && x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.UserId == access.ActorUserId && x.DeletedAt == null, ct)
            ?? throw new ComposeException("draft_not_found");
        if (await db.Outgoing.IgnoreQueryFilters().AnyAsync(o => o.DraftId == draftId && (o.Status == OutgoingStatus.UndoWindow || o.Status == OutgoingStatus.Scheduled || o.Status == OutgoingStatus.Submitting), ct))
        {
            throw new ComposeException("draft_sending");
        }

        d.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (d.MessageId is { } m)
        {
            await store.ApplyAsync(access, [m], new MessageChange { Expunge = true }, ct);
        }
    }
}

/// <summary>Which From addresses a user may use (PROMPT 2.2 step 2): own address, aliases, shared mailboxes with send-as.</summary>
public sealed class SenderPolicy(BlueMailDbContext db)
{
    public sealed record FromOption(string Address, string Name, string Kind);

    public async Task<IReadOnlyList<FromOption>> OptionsAsync(MailboxAccess access, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == access.ActorUserId && u.TenantId == access.TenantId)
            .Select(u => new { u.PrimaryEmail, u.DisplayName }).FirstAsync(ct);
        var list = new List<FromOption> { new(user.PrimaryEmail, user.DisplayName, "primary") };
        list.AddRange(await db.UserAliases.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == access.TenantId && a.UserId == access.ActorUserId && a.DeletedAt == null)
            .OrderBy(a => a.Email).Select(a => new FromOption(a.Email, user.DisplayName, "alias")).ToListAsync(ct));
        list.AddRange(await (from m in db.SharedMailboxMembers.IgnoreQueryFilters()
                             join s in db.SharedMailboxes.IgnoreQueryFilters() on m.SharedMailboxId equals s.Id
                             where m.TenantId == access.TenantId && m.UserId == access.ActorUserId && m.CanSendAs && m.DeletedAt == null && s.DeletedAt == null
                             orderby s.Email
                             select new FromOption(s.Email, s.Name, "shared")).AsNoTracking().ToListAsync(ct));
        return list;
    }

    public async Task<string> DefaultFromAsync(MailboxAccess access, CancellationToken ct) => (await OptionsAsync(access, ct))[0].Address;

    public async Task<bool> MayUseAsync(MailboxAccess access, string from, CancellationToken ct) =>
        (await OptionsAsync(access, ct)).Any(o => o.Address.Equals(from, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Send requests (PROMPT 6.4): sending a draft creates one outgoing row in UNDO_WINDOW (or SCHEDULED); undo and
/// cancel are conditional updates. The unique index ux_outgoing_draft_pending makes "send twice" impossible even
/// with two windows or two clicks.
/// </summary>
public sealed class SendService(BlueMailDbContext db, PgMailStore store, SenderPolicy senders, DraftService drafts, BmSettings settings)
{
    public const int MaxRecipients = 500;
    public static readonly int[] UndoChoices = [5, 10, 20, 30];

    public sealed record SendResult(Guid OutgoingId, Guid DraftId, DateTimeOffset SendAt, bool Scheduled);

    public async Task<SendResult> SendAsync(MailboxAccess access, Guid draftId, DateTimeOffset? scheduleAt, bool archive, int undoSeconds, CancellationToken ct)
    {
        access.Require(MailboxRights.Write);
        var draft = await drafts.GetAsync(access, draftId, ct) ?? throw new ComposeException("draft_not_found");
        var s = draft.State;
        var recipients = s.AllRecipients.ToList();
        if (recipients.Count == 0)
        {
            throw new ComposeException("no_recipients");
        }

        if (recipients.Count > MaxRecipients)
        {
            throw new ComposeException("too_many_recipients");
        }

        if (recipients.FirstOrDefault(r => !EmailAddress.TryParse(r.Address, out _)) is { } bad)
        {
            throw new ComposeException("invalid_recipient:" + bad.Address);
        }

        if (s.From is null || !await senders.MayUseAsync(access, s.From, ct))
        {
            throw new ComposeException("from_not_allowed");
        }

        var now = DateTimeOffset.UtcNow;
        var scheduled = scheduleAt is { } at && at > now.AddSeconds(30);
        var sendAt = scheduled ? scheduleAt!.Value.ToUniversalTime() : now.AddSeconds(UndoChoices.Contains(undoSeconds) ? undoSeconds : 5);
        var outgoing = new Outgoing
        {
            TenantId = access.TenantId,
            UserId = access.ActorUserId,
            MailboxId = access.MailboxId,
            DraftId = draftId,
            FromAddress = s.From,
            Status = scheduled ? OutgoingStatus.Scheduled : OutgoingStatus.UndoWindow,
            SendAt = sendAt,
            RfcMessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            ArchiveThread = archive,
        };
        db.Outgoing.Add(outgoing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw new ComposeException("already_sending");
        }

        if (scheduled && draft.MessageId is { } m)
        {
            await store.ApplyAsync(access, [m], new MessageChange { AddSystem = [SystemLabels.Scheduled], RemoveSystem = [SystemLabels.Drafts] }, ct);
        }

        return new SendResult(outgoing.Id, draftId, sendAt, scheduled);
    }

    /// <summary>Undo (during the undo window) or cancel a scheduled send. False when it is already being sent.</summary>
    public async Task<Guid?> CancelAsync(MailboxAccess access, Guid outgoingId, CancellationToken ct)
    {
        access.Require(MailboxRights.Write);
        var row = await db.Outgoing.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == outgoingId && o.TenantId == access.TenantId && o.UserId == access.ActorUserId, ct);
        if (row is null)
        {
            return null;
        }

        var n = await db.Outgoing.IgnoreQueryFilters()
            .Where(o => o.Id == outgoingId && (o.Status == OutgoingStatus.UndoWindow || o.Status == OutgoingStatus.Scheduled))
            .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OutgoingStatus.Cancelled).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (n == 0)
        {
            return null;
        }

        if (row.Status == OutgoingStatus.Scheduled && await drafts.GetAsync(access, row.DraftId, ct) is { MessageId: { } m })
        {
            await store.ApplyAsync(access, [m], new MessageChange { AddSystem = [SystemLabels.Drafts], RemoveSystem = [SystemLabels.Scheduled] }, ct);
        }

        return row.DraftId;
    }

    public async Task<IReadOnlyList<Outgoing>> PendingAsync(MailboxAccess access, CancellationToken ct) =>
        await db.Outgoing.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == access.TenantId && o.UserId == access.ActorUserId &&
                        (o.Status == OutgoingStatus.UndoWindow || o.Status == OutgoingStatus.Scheduled || o.Status == OutgoingStatus.Submitting))
            .OrderBy(o => o.SendAt).ToListAsync(ct);
}
