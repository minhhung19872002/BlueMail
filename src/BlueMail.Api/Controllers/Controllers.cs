using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Account;
using BlueMail.Application.Features.Admin.Tenants;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Me;
using BlueMail.Application.Web;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

// Controllers only translate HTTP <-> MediatR; no business logic here (PROMPT 12).

[ApiController]
[Route("api/v1/me")]
public sealed class MeController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [SelfGuarded("Returns the caller's own profile only.")]
    public async Task<ApiResponse<MeDto>> Get(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetMeQuery(), ct));

    [HttpGet("settings")]
    [SelfGuarded("The caller's own settings only.")]
    public async Task<ApiResponse<BlueMail.Application.Features.Me.MailSettings>> Settings(CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Me.GetSettingsQuery(), ct));

    [HttpPut("settings")]
    [SelfGuarded("The caller's own settings only.")]
    public async Task<ApiResponse<BlueMail.Application.Features.Me.MailSettings>> SaveSettings([FromBody] BlueMail.Application.Features.Me.MailSettings body,
        CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Me.SaveSettingsCommand(body), ct));

    [HttpPut("profile")]
    [SelfGuarded("The caller's own profile only.")]
    public async Task<ApiResponse<bool>> SaveProfile([FromBody] BlueMail.Application.Features.Me.SaveProfileCommand body, CancellationToken ct)
    {
        await mediator.Send(body, ct);
        return ApiResponses.Ok(true);
    }
}

[ApiController]
[Route("api/v1/mail")]
public sealed class MailController(ISender mediator) : ControllerBase
{
    private const string Guarded = "Mailbox access is checked by IMailStore.OpenAsync (own mailbox or granted shared mailbox).";

    [HttpGet("labels")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<LabelsDto>> Labels([FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetLabelsQuery(mailboxId), ct));

    public sealed record LabelBody(string Name, Guid? ParentId, string? ColorBg, string? ColorText, string? ShowInList = null, bool? ShowInMessageList = null);

    [HttpPost("labels")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<Guid>> CreateLabel([FromQuery] Guid? mailboxId, [FromBody] LabelBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new SaveLabelCommand(mailboxId, null, body.Name, body.ParentId, body.ColorBg, body.ColorText), ct));

    [HttpPut("labels/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<Guid>> UpdateLabel(Guid id, [FromQuery] Guid? mailboxId, [FromBody] LabelBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new SaveLabelCommand(mailboxId, id, body.Name, null, body.ColorBg, body.ColorText, body.ShowInList, body.ShowInMessageList), ct));

    [HttpDelete("labels/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<bool>> DeleteLabel(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct)
    {
        await mediator.Send(new DeleteLabelCommand(mailboxId, id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("conversations")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.MailStore.ConversationPage>> Conversations([FromQuery] Guid? mailboxId, [FromQuery] Guid? labelId, [FromQuery] string? view,
        [FromQuery] bool unreadOnly = false, [FromQuery] bool threads = true, [FromQuery] string? cursor = null, [FromQuery] int limit = 50, [FromQuery] string? q = null,
        CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new ListConversationsQuery(mailboxId, labelId, view, unreadOnly, threads, cursor, limit, q), ct));

    [HttpGet("threads/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<ThreadDto>> Thread(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetThreadQuery(mailboxId, id), ct));

    [HttpGet("messages/{id:guid}/body")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<MessageBodyDto>> Body(Guid id, [FromQuery] Guid? mailboxId, [FromQuery] bool showImages = false, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new GetMessageBodyQuery(mailboxId, id, showImages), ct));

    [HttpGet("messages/{id:guid}/original")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<OriginalDto>> Original(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetOriginalQuery(mailboxId, id), ct));

    [HttpGet("snoozed")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<IReadOnlyList<BlueMail.Application.Features.Mail.SnoozedDto>>> Snoozed([FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Mail.ListSnoozedQuery(mailboxId), ct));

    [HttpPost("messages/{id:guid}/unsubscribe")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.Application.Features.Mail.UnsubscribeResult>> Unsubscribe(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Mail.UnsubscribeCommand(id, mailboxId), ct));

    [HttpPost("messages/{id:guid}/trust-images")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<bool>> TrustImages(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct)
    {
        await mediator.Send(new TrustImageSenderCommand(mailboxId, id), ct);
        return ApiResponses.Ok(true);
    }

    public sealed record ActionBody(string Action, IReadOnlyList<Guid>? ThreadIds, IReadOnlyList<Guid>? MessageIds, Guid? LabelId, Guid? FromLabelId,
        DateTimeOffset? SnoozeUntil = null);

    [HttpPost("actions")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.MailStore.ChangeResult>> Action([FromQuery] Guid? mailboxId, [FromBody] ActionBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new MailActionCommand(mailboxId, body.Action, body.ThreadIds ?? [], body.MessageIds ?? [], body.LabelId, body.FromLabelId,
            body.SnoozeUntil), ct));

    public sealed record BulkJobBody(string Action, Guid? LabelId, Guid? FromLabelId, DateTimeOffset? SnoozeUntil, Guid? ScopeLabelId, string? Query);

    public sealed record InviteResponseBody(string Response, bool SendReply = true);

    /// <summary>An action on every conversation of a list or search, run in the background (PROMPT 8.3).</summary>
    [HttpPost("bulk-jobs")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.Application.Features.Mail.BulkJobDto>> StartBulkJob([FromQuery] Guid? mailboxId, [FromBody] BulkJobBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Mail.StartBulkJobCommand(mailboxId, body.Action, body.LabelId, body.FromLabelId, body.SnoozeUntil,
            body.ScopeLabelId, body.Query), ct));

    [HttpGet("bulk-jobs/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.Application.Features.Mail.BulkJobDto>> BulkJob(Guid id, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Mail.GetBulkJobQuery(id), ct));

    /// <summary>The meeting invitation in a message (iMIP card, PROMPT 6.3); null data when it carries none.</summary>
    [HttpGet("messages/{id:guid}/invite")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.Application.Features.Calendar.InviteDto?>> Invite(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Calendar.GetInviteQuery(mailboxId, id), ct));

    /// <summary>Yes / Maybe / No to an invitation: recorded, and the iTIP REPLY goes to the organizer unless sendReply is false.</summary>
    [HttpPost("messages/{id:guid}/invite/respond")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.Application.Features.Calendar.InviteDto>> RespondInvite(Guid id, [FromQuery] Guid? mailboxId, [FromBody] InviteResponseBody body,
        CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Calendar.RespondInviteCommand(mailboxId, id, body.Response, body.SendReply), ct));

    [HttpGet("changes")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<BlueMail.MailStore.MailChanges>> Changes([FromQuery] Guid? mailboxId, [FromQuery] long sinceModseq, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetChangesQuery(mailboxId, sinceModseq), ct));
}

[ApiController]
[Route("api/v1/admin/spam")]
public sealed class AdminSpamController(ISender mediator) : ControllerBase
{
    [HttpGet("settings")]
    [RequirePermission(Permissions.AdminSpamEdit)]
    public async Task<ApiResponse<BlueMail.Application.Features.Antispam.SpamSettingsDto>> Settings(CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Antispam.GetSpamSettingsQuery(), ct));

    [HttpPut("settings")]
    [RequirePermission(Permissions.AdminSpamEdit)]
    public async Task<ApiResponse<bool>> SaveSettings([FromBody] BlueMail.Application.Features.Antispam.SaveSpamSettingsCommand body, CancellationToken ct)
    {
        await mediator.Send(body, ct);
        return ApiResponses.Ok(true);
    }

    public sealed record ToggleBody(bool Enabled);

    [HttpPut("lists/{id:int}")]
    [RequirePermission(Permissions.AdminSpamEdit)]
    public async Task<ApiResponse<bool>> ToggleList(int id, [FromBody] ToggleBody body, CancellationToken ct)
    {
        await mediator.Send(new BlueMail.Application.Features.Antispam.ToggleDnsblCommand(id, body.Enabled), ct);
        return ApiResponses.Ok(true);
    }
}

[ApiController]
[Route("api/v1/admin/quarantine")]
public sealed class AdminQuarantineController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.AdminQuarantineView)]
    public async Task<ApiResponse<Paged<BlueMail.Application.Features.Antispam.QuarantineItemDto>>> List([FromQuery] string? status, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Antispam.ListQuarantineQuery(status, page, pageSize), ct));

    [HttpPost("{id:guid}/release")]
    [RequirePermission(Permissions.AdminQuarantineEdit)]
    public async Task<ApiResponse<bool>> Release(Guid id, CancellationToken ct)
    {
        await mediator.Send(new BlueMail.Application.Features.Antispam.DecideQuarantineCommand(id, true), ct);
        return ApiResponses.Ok(true);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.AdminQuarantineEdit)]
    public async Task<ApiResponse<bool>> Delete(Guid id, CancellationToken ct)
    {
        await mediator.Send(new BlueMail.Application.Features.Antispam.DecideQuarantineCommand(id, false), ct);
        return ApiResponses.Ok(true);
    }
}

/// <summary>
/// Content fetched by the browser without the bearer token (images in the sandboxed iframe, downloads).
/// The signed token is the credential; access to the mailbox is re-checked when serving.
/// </summary>
[ApiController]
[Route("api/v1/mail")]
public sealed class MailContentController(SignedContent content) : ControllerBase
{
    [HttpGet("p/{token}")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public Task<IActionResult> Part(string token, CancellationToken ct) => Serve(token, BlueMail.Infrastructure.Security.SignedKind.Part, ct);

    [HttpGet("img/{token}")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public Task<IActionResult> Image(string token, CancellationToken ct) => Serve(token, BlueMail.Infrastructure.Security.SignedKind.Image, ct);

    private async Task<IActionResult> Serve(string token, BlueMail.Infrastructure.Security.SignedKind kind, CancellationToken ct)
    {
        var c = await content.ServeAsync(token, kind, ct);
        // Our origin serves untrusted bytes: never sniff, never run anything, keep private.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox; style-src 'unsafe-inline'";
        Response.Headers.CacheControl = "private, max-age=3600";
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue(c.Download ? "attachment" : "inline");
        if (c.FileName is not null)
        {
            cd.FileNameStar = c.FileName;
            cd.FileName = new string(c.FileName.Select(ch => ch < 128 && ch >= 32 && ch != '"' ? ch : '_').ToArray());
        }

        Response.Headers.ContentDisposition = cd.ToString();
        return File(c.Content, c.ContentType);
    }
}

[ApiController]
[Route("api/v1/account/sessions")]
public sealed class AccountSessionsController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [SelfGuarded("Lists the caller's own sessions only.")]
    public async Task<ApiResponse<IReadOnlyList<SessionDto>>> List(CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ListMySessionsQuery(), ct));

    [HttpDelete("{id:guid}")]
    [SelfGuarded("Revokes a session of the caller only (filtered by user id in the handler).")]
    public async Task<ApiResponse<bool>> Revoke(Guid id, CancellationToken ct)
    {
        await mediator.Send(new RevokeSessionCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}

[ApiController]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.AdminUsersView)]
    public async Task<ApiResponse<Paged<UserDto>>> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new ListUsersQuery(search, page, pageSize), ct));

    [HttpPost]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<UserDto>> Create([FromBody] CreateUserCommand command, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(command, ct));

    public sealed record StatusBody(UserStatus Status);

    [HttpPut("{id:guid}/status")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> SetStatus(Guid id, [FromBody] StatusBody body, CancellationToken ct)
    {
        await mediator.Send(new SetUserStatusCommand(id, body.Status), ct);
        return ApiResponses.Ok(true);
    }
}

[ApiController]
[Route("api/v1/admin/tenants")]
public sealed class AdminTenantsController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PlatformTenantsView)]
    public async Task<ApiResponse<Paged<TenantDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new ListTenantsQuery(page, pageSize), ct));

    [HttpPost]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<Guid>> Create([FromBody] CreateTenantCommand command, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(command, ct));

    public sealed record StatusBody(TenantStatus Status);

    [HttpPut("{id:guid}/status")]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<bool>> SetStatus(Guid id, [FromBody] StatusBody body, CancellationToken ct)
    {
        await mediator.Send(new SetTenantStatusCommand(id, body.Status), ct);
        return ApiResponses.Ok(true);
    }
}
