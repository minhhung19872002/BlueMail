using System.Text;
using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Contacts;
using BlueMail.Application.Features.Rules;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>Personal contacts, groups and the organisation directory (PROMPT 6.10).</summary>
[ApiController]
[Route("api/v1/contacts")]
public sealed class ContactsController(ISender mediator) : ControllerBase
{
    private const string Own = "acts only on the caller's own contacts";

    [HttpGet]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Paged<ContactDto>>> List([FromQuery] string? q, [FromQuery] Guid? groupId, [FromQuery] bool other = false, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        ApiResponses.Ok(await mediator.Send(new ListContactsQuery(q, groupId, other, page, pageSize), ct));

    [HttpPost]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> Create([FromBody] SaveContactCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> Update(Guid id, [FromBody] SaveContactCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    public sealed record IdsBody(IReadOnlyList<Guid> Ids);

    [HttpPost("delete")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<int>> Delete([FromBody] IdsBody body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new DeleteContactsCommand(body.Ids), ct));

    [HttpPut("{id:guid}/photo")]
    [SelfGuarded(Own)]
    [RequestSizeLimit(2_000_000)]
    public async Task<ApiResponse<bool>> SetPhoto(Guid id, IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        await mediator.Send(new SetContactPhotoCommand(id, ms.ToArray(), file.ContentType), ct);
        return ApiResponses.Ok(true);
    }

    [HttpDelete("{id:guid}/photo")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> DeletePhoto(Guid id, CancellationToken ct)
    {
        await mediator.Send(new SetContactPhotoCommand(id, null, null), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("{id:guid}/photo")]
    [SelfGuarded(Own)]
    public async Task<IActionResult> Photo(Guid id, CancellationToken ct)
    {
        var p = await mediator.Send(new GetContactPhotoQuery(id), ct);
        if (p is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=300";
        return File(p.Value.Content, p.Value.ContentType);
    }

    [HttpGet("groups")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<IReadOnlyList<ContactGroupDto>>> Groups(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListContactGroupsQuery(), ct));

    [HttpPost("groups")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> CreateGroup([FromBody] SaveContactGroupCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("groups/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> RenameGroup(Guid id, [FromBody] SaveContactGroupCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    [HttpDelete("groups/{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> DeleteGroup(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteContactGroupCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpPost("import")]
    [SelfGuarded(Own)]
    [RequestSizeLimit(20_000_000)]
    public async Task<ApiResponse<ImportContactsResult>> Import(IFormFile file, [FromQuery] Guid? groupId, CancellationToken ct)
    {
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ApiResponses.Ok(await mediator.Send(new ImportContactsCommand(file.FileName, await reader.ReadToEndAsync(ct), groupId), ct));
    }

    [HttpGet("export")]
    [SelfGuarded(Own)]
    public async Task<IActionResult> Export([FromQuery] string format = "vcf", [FromQuery] Guid? groupId = null, CancellationToken ct = default)
    {
        var (content, type, name) = await mediator.Send(new ExportContactsQuery(format == "csv" ? "csv" : "vcf", groupId), ct);
        return File(content, type, name);
    }

    [HttpPost("merge-duplicates")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<int>> Merge(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new MergeDuplicatesCommand(), ct));

    [HttpGet("directory")]
    [SelfGuarded("the caller's own organisation, filtered by the tenant query filter")]
    public async Task<ApiResponse<IReadOnlyList<DirectoryEntryDto>>> Directory([FromQuery] string? q, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ListDirectoryQuery(q), ct));
}

[ApiController]
[Route("api/v1/admin")]
public sealed class AdminOrganizeController(ISender mediator) : ControllerBase
{
    public sealed record HiddenBody(bool Hidden);

    [HttpPut("directory/{userId:guid}")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    public async Task<ApiResponse<bool>> SetHidden(Guid userId, [FromBody] HiddenBody body, CancellationToken ct)
    {
        await mediator.Send(new SetDirectoryVisibilityCommand(userId, body.Hidden), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("category-rules")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<IReadOnlyList<CategoryRuleDto>>> CategoryRules(CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ListCategoryRulesQuery(), ct));

    [HttpPost("category-rules")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<int>> AddCategoryRule([FromBody] AddCategoryRuleCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("category-rules/{id:int}")]
    [RequirePermission(Permissions.AdminPolicyEdit)]
    public async Task<ApiResponse<bool>> DeleteCategoryRule(int id, CancellationToken ct)
    {
        await mediator.Send(new DeleteCategoryRuleCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}
