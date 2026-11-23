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
}

[ApiController]
[Route("api/v1/mail")]
public sealed class MailController(ISender mediator) : ControllerBase
{
    [HttpGet("labels")]
    [SelfGuarded("Mailbox access is checked by IMailStore.OpenAsync (own mailbox or granted shared mailbox).")]
    public async Task<ApiResponse<LabelsDto>> Labels([FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetLabelsQuery(mailboxId), ct));
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
