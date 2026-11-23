using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Admin.Domains;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>Domains of the tenant: wizard records, live DNS checks, aliases, catch-all, DKIM rotation (PROMPT 7.2).</summary>
[ApiController]
[Route("api/v1/admin/domains")]
public sealed class AdminDomainsController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.AdminDomainsView)]
    public async Task<ApiResponse<IReadOnlyList<DomainDto>>> List(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListDomainsQuery(), ct));

    [HttpGet("{id:guid}/records")]
    [RequirePermission(Permissions.AdminDomainsView)]
    public async Task<ApiResponse<DomainRecordsDto>> Records(Guid id, [FromQuery] bool check = false, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new GetDomainRecordsQuery(id, check), ct));

    [HttpPost]
    [RequirePermission(Permissions.AdminDomainsEdit)]
    public async Task<ApiResponse<Guid>> Add([FromBody] AddDomainCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.AdminDomainsEdit)]
    public async Task<ApiResponse<bool>> Delete(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteDomainCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    public sealed record CatchAllBody(string? Target);

    [HttpPut("{id:guid}/catch-all")]
    [RequirePermission(Permissions.AdminDomainsEdit)]
    public async Task<ApiResponse<bool>> CatchAll(Guid id, [FromBody] CatchAllBody body, CancellationToken ct)
    {
        await mediator.Send(new SetCatchAllCommand(id, body.Target), ct);
        return ApiResponses.Ok(true);
    }

    [HttpPost("{id:guid}/dkim/rotate")]
    [RequirePermission(Permissions.AdminDomainsEdit)]
    public async Task<ApiResponse<DkimKeyDto>> Rotate(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new RotateDkimCommand(id), ct));
}

/// <summary>Policies of the tenant or of an organisational unit, and routing rules (PROMPT 7.4).</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminPoliciesController(ISender mediator) : ControllerBase
{
    [HttpGet("policies")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<IReadOnlyList<BlueMail.Application.Features.Admin.Policies.PolicyValueDto>>> Policies([FromQuery] Guid? orgUnitId,
        CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Admin.Policies.ListPoliciesQuery(orgUnitId), ct));

    public sealed record PolicyBody(Guid? OrgUnitId, System.Text.Json.JsonElement? Value);

    [HttpPut("policies/{key}")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<bool>> SetPolicy(string key, [FromBody] PolicyBody body, CancellationToken ct)
    {
        await mediator.Send(new BlueMail.Application.Features.Admin.Policies.SetPolicyCommand(key, body.OrgUnitId, body.Value), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("routing-rules")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<IReadOnlyList<BlueMail.Application.Features.Admin.Policies.RoutingRuleDto>>> Rules(CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new BlueMail.Application.Features.Admin.Policies.ListRoutingRulesQuery(), ct));

    [HttpPost("routing-rules")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<Guid>> AddRule([FromBody] BlueMail.Application.Features.Admin.Policies.SaveRoutingRuleCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("routing-rules/{id:guid}")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<Guid>> UpdateRule(Guid id, [FromBody] BlueMail.Application.Features.Admin.Policies.SaveRoutingRuleCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    [HttpDelete("routing-rules/{id:guid}")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<bool>> DeleteRule(Guid id, CancellationToken ct)
    {
        await mediator.Send(new BlueMail.Application.Features.Admin.Policies.DeleteRoutingRuleCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}
