using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Admin.Directory;
using BlueMail.Application.Features.Admin.Operations;
using BlueMail.Application.Features.Admin.Tenants;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>Users (details, edit, password, aliases, address, delete, CSV import), groups, shared mailboxes, units (PROMPT 7.3).</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminDirectoryController(ISender mediator) : ControllerBase
{
    [HttpGet("users/{id:guid}")]
    [RequirePermission(Permissions.AdminUsersView)]
    public async Task<ApiResponse<UserDetailDto>> GetUser(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetUserQuery(id), ct));

    [HttpPut("users/{id:guid}")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> UpdateUser(Guid id, [FromBody] UpdateUserCommand body, CancellationToken ct)
    {
        await mediator.Send(body with { Id = id }, ct);
        return ApiResponses.Ok(true);
    }

    [HttpPost("users/{id:guid}/reset-password")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<string>> ResetPassword(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ResetPasswordCommand(id), ct));

    public sealed record EmailBody(string Email);

    [HttpPost("users/{id:guid}/aliases")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> AddAlias(Guid id, [FromBody] EmailBody body, CancellationToken ct)
    {
        await mediator.Send(new AddAliasCommand(id, body.Email), ct);
        return ApiResponses.Ok(true);
    }

    [HttpDelete("users/{id:guid}/aliases/{email}")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> RemoveAlias(Guid id, string email, CancellationToken ct)
    {
        await mediator.Send(new RemoveAliasCommand(id, email), ct);
        return ApiResponses.Ok(true);
    }

    [HttpPut("users/{id:guid}/address")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> ChangeAddress(Guid id, [FromBody] EmailBody body, CancellationToken ct)
    {
        await mediator.Send(new ChangePrimaryAddressCommand(id, body.Email), ct);
        return ApiResponses.Ok(true);
    }

    [HttpDelete("users/{id:guid}")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<int>> DeleteUser(Guid id, [FromQuery] Guid? transferTo, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new DeleteUserCommand(id, transferTo), ct));

    [HttpPost("users/import/parse")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [RequestSizeLimit(5_000_000)]
    public async Task<ApiResponse<IReadOnlyList<ImportUserRow>>> ParseCsv(IFormFile file, CancellationToken ct)
    {
        using var r = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ApiResponses.Ok(await mediator.Send(new ParseUsersCsvCommand(await r.ReadToEndAsync(ct)), ct));
    }

    [HttpPost("users/import")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<ImportUsersResult>> Import([FromBody] ImportUsersCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpGet("groups")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<IReadOnlyList<GroupDto>>> Groups(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListGroupsQuery(), ct));

    [HttpPost("groups")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<Guid>> CreateGroup([FromBody] SaveGroupCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("groups/{id:guid}")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<Guid>> UpdateGroup(Guid id, [FromBody] SaveGroupCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    [HttpDelete("groups/{id:guid}")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<bool>> DeleteGroup(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteGroupCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    public sealed record MemberBody(string Email, string Role);

    [HttpPost("groups/{id:guid}/members")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<Guid>> AddMember(Guid id, [FromBody] MemberBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new AddGroupMemberCommand(id, body.Email, body.Role), ct));

    [HttpDelete("groups/{id:guid}/members/{memberId:guid}")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<bool>> RemoveMember(Guid id, Guid memberId, CancellationToken ct)
    {
        await mediator.Send(new RemoveGroupMemberCommand(id, memberId), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("shared-mailboxes")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<IReadOnlyList<SharedMailboxDto>>> Shared(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListSharedMailboxesQuery(), ct));

    [HttpPost("shared-mailboxes")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<Guid>> CreateShared([FromBody] CreateSharedMailboxCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpPut("shared-mailboxes/{id:guid}/members")]
    [RequirePermission(Permissions.AdminGroupsEdit)]
    public async Task<ApiResponse<bool>> SetSharedMember(Guid id, [FromBody] SetSharedMemberCommand body, CancellationToken ct)
    {
        await mediator.Send(body with { SharedMailboxId = id }, ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("org-units")]
    [RequirePermission(Permissions.AdminUsersView)]
    public async Task<ApiResponse<IReadOnlyList<OrgUnitDto>>> OrgUnits(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListOrgUnitsQuery(), ct));
}

/// <summary>Mail operations: trace, queue, suppressions, sending health, reports (PROMPT 7.6).</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminOperationsController(ISender mediator) : ControllerBase
{
    [HttpGet("trace")]
    [RequirePermission(Permissions.AdminTraceView)]
    public async Task<ApiResponse<Paged<TraceRowDto>>> Trace([FromQuery] string? sender, [FromQuery] string? recipient, [FromQuery] string? messageId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new TraceQuery(sender, recipient, messageId, from, to, page, pageSize), ct));

    [HttpGet("trace/{id:long}")]
    [RequirePermission(Permissions.AdminTraceView)]
    public async Task<ApiResponse<TraceDetailDto>> TraceDetail(long id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetTraceDetailQuery(id), ct));

    [HttpGet("queue")]
    [RequirePermission(Permissions.AdminQueueEdit)]
    public async Task<ApiResponse<Paged<QueueItemDto>>> Queue([FromQuery] string? status, [FromQuery] string? domain, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => ApiResponses.Ok(await mediator.Send(new ListQueueQuery(status, domain, page, pageSize), ct));

    public sealed record IdsBody(IReadOnlyList<long> Ids);

    [HttpPost("queue/retry")]
    [RequirePermission(Permissions.AdminQueueEdit)]
    public async Task<ApiResponse<int>> Retry([FromBody] IdsBody body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new QueueActionCommand(body.Ids, false), ct));

    [HttpPost("queue/cancel")]
    [RequirePermission(Permissions.AdminQueueEdit)]
    public async Task<ApiResponse<int>> Cancel([FromBody] IdsBody body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new QueueActionCommand(body.Ids, true), ct));

    [HttpGet("suppressions")]
    [RequirePermission(Permissions.AdminQueueEdit)]
    public async Task<ApiResponse<Paged<SuppressionDto>>> Suppressions([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => ApiResponses.Ok(await mediator.Send(new ListSuppressionsQuery(search, page, pageSize), ct));

    [HttpDelete("suppressions/{id:long}")]
    [RequirePermission(Permissions.AdminQueueEdit)]
    public async Task<ApiResponse<bool>> RemoveSuppression(long id, CancellationToken ct)
    {
        await mediator.Send(new RemoveSuppressionCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("health")]
    [RequirePermission(Permissions.AdminReportsView)]
    public async Task<ApiResponse<SendingHealthDto>> Health([FromQuery] int days = 7, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new SendingHealthQuery(days), ct));

    [HttpGet("reports/{kind}")]
    [RequirePermission(Permissions.AdminReportsView)]
    public async Task<ApiResponse<ReportDto>> Report(string kind, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ReportQuery(kind.ToUpperInvariant(), from, to), ct));

    [HttpGet("reports/{kind}/xlsx")]
    [RequirePermission(Permissions.AdminReportsView)]
    public async Task<IActionResult> ReportXlsx(string kind, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var (content, name) = await mediator.Send(new ExportReportQuery(kind.ToUpperInvariant(), from, to), ct);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }
}

/// <summary>Platform administration of tenants (PROMPT 7.1).</summary>
[ApiController]
[Route("api/v1/admin/tenants")]
public sealed class PlatformTenantsController(ISender mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.PlatformTenantsView)]
    public async Task<ApiResponse<TenantDetailDto>> Get(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetTenantQuery(id), ct));

    [HttpGet("{id:guid}/users")]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<IReadOnlyList<TenantUserDto>>> Users(Guid id, [FromQuery] string? search, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ListTenantUsersQuery(id, search), ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<bool>> Update(Guid id, [FromBody] UpdateTenantCommand body, CancellationToken ct)
    {
        await mediator.Send(body with { Id = id }, ct);
        return ApiResponses.Ok(true);
    }

    public sealed record SupportBody(Guid UserId, string Reason);

    [HttpPost("{id:guid}/support-signin")]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<string>> Support(Guid id, [FromBody] SupportBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new StartSupportSignInCommand(id, body.UserId, body.Reason), ct));
}
