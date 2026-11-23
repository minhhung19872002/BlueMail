using BlueMail.Api.Auth;
using BlueMail.Application.Web;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Rules;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>Filters, blocked senders, vacation reply and forwarding of the caller's own mailbox (PROMPT 6.8).</summary>
[ApiController]
[Route("api/v1/mail")]
public sealed class RulesController(ISender mediator) : ControllerBase
{
    private const string Own = "acts only on the caller's own mailbox and settings";

    [HttpGet("filters")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<IReadOnlyList<FilterDto>>> Filters(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListFiltersQuery(), ct));

    [HttpPost("filters")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> CreateFilter([FromBody] SaveFilterCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("filters/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> UpdateFilter(Guid id, [FromBody] SaveFilterCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    [HttpDelete("filters/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> DeleteFilter(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteFilterCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    public sealed record OrderBody(IReadOnlyList<Guid> Ids);

    [HttpPut("filters/order")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> Reorder([FromBody] OrderBody body, CancellationToken ct)
    {
        await mediator.Send(new ReorderFiltersCommand(body.Ids), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("filters/matches")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<int>> Matches([FromQuery] string q, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new CountMatchesQuery(q), ct));

    [HttpPost("filters/{id:guid}/apply")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<FilterJobDto>> Apply(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ApplyFilterCommand(id), ct));

    [HttpGet("filter-jobs/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<FilterJobDto>> Job(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetFilterJobQuery(id), ct));

    [HttpGet("filters/export")]
    [SelfGuarded(Own)]
    public async Task<IActionResult> Export(CancellationToken ct) =>
        File(System.Text.Encoding.UTF8.GetBytes(await mediator.Send(new ExportFiltersQuery(), ct)), "application/xml", "mailFilters.xml");

    [HttpPost("filters/import")]
    [SelfGuarded(Own)]
    [RequestSizeLimit(2_000_000)]
    public async Task<ApiResponse<ImportResultDto>> Import(IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return ApiResponses.Ok(await mediator.Send(new ImportFiltersCommand(s), ct));
    }

    [HttpGet("blocked")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<IReadOnlyList<BlockedSenderDto>>> Blocked(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListBlockedQuery(), ct));

    [HttpPost("blocked")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> Block([FromBody] BlockSenderCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("blocked/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> Unblock(Guid id, CancellationToken ct)
    {
        await mediator.Send(new UnblockSenderCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("vacation")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<VacationDto>> Vacation(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetVacationQuery(), ct));

    [HttpPut("vacation")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> SaveVacation([FromBody] SaveVacationCommand body, CancellationToken ct)
    {
        await mediator.Send(body, ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("forwarding")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<ForwardingListDto>> Forwarding(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListForwardingQuery(), ct));

    [HttpPost("forwarding")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> AddForwarding([FromBody] AddForwardingCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    public sealed record CodeBody(string Code);

    [HttpPost("forwarding/{id:guid}/verify")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> Verify(Guid id, [FromBody] CodeBody body, CancellationToken ct)
    {
        await mediator.Send(new VerifyForwardingCommand(id, body.Code), ct);
        return ApiResponses.Ok(true);
    }

    [HttpPut("forwarding/active")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> SetActive([FromBody] SetActiveForwardingCommand body, CancellationToken ct)
    {
        await mediator.Send(body, ct);
        return ApiResponses.Ok(true);
    }

    [HttpDelete("forwarding/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> DeleteForwarding(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteForwardingCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}

[ApiController]
[Route("api/v1/admin/policy")]
public sealed class AdminPolicyController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<MailPolicyDto>> Get(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new GetMailPolicyQuery(), ct));

    [HttpPut]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<bool>> Save([FromBody] SaveMailPolicyCommand body, CancellationToken ct)
    {
        await mediator.Send(body, ct);
        return ApiResponses.Ok(true);
    }
}
