using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Compliance;
using BlueMail.Application.Features.Migration;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>The caller's own imports (PROMPT 7.7): from IMAP or an uploaded mbox / .eml / .zip.</summary>
[ApiController]
[Route("api/v1/me/imports")]
public sealed class MyImportsController(ISender mediator) : ControllerBase
{
    private const string Own = "Imports into the caller's own mailbox; jobs are filtered by user id in the handler.";

    [HttpPost("test")]
    [SelfGuarded("Only tries to sign in to the given server with the given account; no data is stored.")]
    public async Task<ApiResponse<ImapTestResult>> Test([FromBody] ImapSourceDto body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new TestImapSourceCommand(body), ct));

    [HttpPost]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> Start([FromBody] ImapSourceDto body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new StartImapImportCommand(null, body), ct));

    public sealed record FileBody(Guid UploadId);

    [HttpPost("file")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<Guid>> File([FromBody] FileBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new StartFileImportCommand(null, body.UploadId), ct));

    [HttpGet]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<IReadOnlyList<ImportJobDto>>> List(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListImportsQuery(false, null), ct));

    [HttpGet("{id:guid}/errors.csv")]
    [SelfGuarded("The job's owner, its requester or a migration administrator (checked in the handler).")]
    public async Task<IActionResult> Errors(Guid id, CancellationToken ct) =>
        File(await mediator.Send(new ImportErrorsCsvQuery(id), ct), "text/csv; charset=utf-8", $"import-{id:N}-errors.csv");

    [HttpPost("{id:guid}/cancel")]
    [SelfGuarded("The job's owner, its requester or a migration administrator (checked in the handler).")]
    public async Task<ApiResponse<bool>> Cancel(Guid id, CancellationToken ct)
    {
        await mediator.Send(new CancelImportCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}

/// <summary>Imports for other users, one by one or as a CSV batch (PROMPT 7.7).</summary>
[ApiController]
[Route("api/v1/admin/imports")]
public sealed class AdminImportsController(ISender mediator) : ControllerBase
{
    public sealed record AdminImapBody(Guid UserId, ImapSourceDto Source);

    [HttpPost]
    [RequirePermission(Permissions.AdminMigrationEdit)]
    public async Task<ApiResponse<Guid>> Start([FromBody] AdminImapBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new StartImapImportCommand(body.UserId, body.Source), ct));

    [HttpPost("batch")]
    [RequirePermission(Permissions.AdminMigrationEdit)]
    [RequestSizeLimit(5_000_000)]
    public async Task<ApiResponse<ImportBatchResult>> Batch(IFormFile file, CancellationToken ct)
    {
        using var r = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ApiResponses.Ok(await mediator.Send(new StartImportBatchCommand(await r.ReadToEndAsync(ct)), ct));
    }

    [HttpGet]
    [RequirePermission(Permissions.AdminMigrationEdit)]
    public async Task<ApiResponse<IReadOnlyList<ImportJobDto>>> List([FromQuery] Guid? batchId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ListImportsQuery(true, batchId), ct));
}

/// <summary>Legal holds, retention, eDiscovery and exports (PROMPT 7.5).</summary>
[ApiController]
[Route("api/v1/admin/compliance")]
public sealed class AdminComplianceController(ISender mediator) : ControllerBase
{
    [HttpGet("holds")]
    [RequirePermission(Permissions.AdminComplianceView)]
    public async Task<ApiResponse<IReadOnlyList<LegalHoldDto>>> Holds(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListLegalHoldsQuery(), ct));

    [HttpPost("holds")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<Guid>> CreateHold([FromBody] CreateLegalHoldCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpPost("holds/{id:guid}/release")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<bool>> Release(Guid id, CancellationToken ct)
    {
        await mediator.Send(new ReleaseLegalHoldCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("retention")]
    [RequirePermission(Permissions.AdminComplianceView)]
    public async Task<ApiResponse<IReadOnlyList<RetentionRuleDto>>> Retention(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListRetentionRulesQuery(), ct));

    [HttpPost("retention")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<Guid>> CreateRule([FromBody] SaveRetentionRuleCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = null }, ct));

    [HttpPut("retention/{id:guid}")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<Guid>> UpdateRule(Guid id, [FromBody] SaveRetentionRuleCommand body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(body with { Id = id }, ct));

    [HttpDelete("retention/{id:guid}")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<bool>> DeleteRule(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteRetentionRuleCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpPost("ediscovery/search")]
    [RequirePermission(Permissions.AdminComplianceView)]
    public async Task<ApiResponse<EDiscoveryResult>> Search([FromBody] EDiscoverySearchQuery body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpPost("ediscovery/export")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<Guid>> Export([FromBody] RequestEDiscoveryExportCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpPost("users/{id:guid}/export")]
    [RequirePermission(Permissions.AdminComplianceEdit)]
    public async Task<ApiResponse<Guid>> UserExport(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new RequestUserExportCommand(id), ct));

    [HttpGet("exports")]
    [RequirePermission(Permissions.AdminComplianceView)]
    public async Task<ApiResponse<IReadOnlyList<DataExportDto>>> Exports(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListExportsQuery(true), ct));
}

/// <summary>"Download my data" and the download links of exports (PROMPT 7.7).</summary>
[ApiController]
[Route("api/v1")]
public sealed class ExportsController(ISender mediator, ExportDownloads downloads) : ControllerBase
{
    [HttpPost("me/export")]
    [SelfGuarded("Exports the caller's own data.")]
    public async Task<ApiResponse<Guid>> RequestMine(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new RequestUserExportCommand(null), ct));

    [HttpGet("me/exports")]
    [SelfGuarded("Exports the caller requested.")]
    public async Task<ApiResponse<IReadOnlyList<DataExportDto>>> Mine(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListExportsQuery(false), ct));

    [HttpPost("exports/{id:guid}/link")]
    [SelfGuarded("Only the requester of the export gets a link (checked in the handler).")]
    public async Task<ApiResponse<string>> Link(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ExportLinkQuery(id), ct));

    [HttpPost("admin/tenants/{id:guid}/export")]
    [RequirePermission(Permissions.PlatformTenantsEdit)]
    public async Task<ApiResponse<Guid>> Tenant(Guid id, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new RequestTenantExportCommand(id), ct));

    [HttpGet("exports/d/{token}")]
    [AllowAnonymous]
    public async Task Download(string token, CancellationToken ct)
    {
        var f = await downloads.OpenAsync(token, ct);
        if (f is null)
        {
            await Problems.WriteAsync(HttpContext, System.Net.HttpStatusCode.NotFound, "link_expired");
            return;
        }

        Response.ContentType = "application/zip";
        Response.ContentLength = f.Value.Size;
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = f.Value.FileName }.ToString();
        await f.Value.Write(Response.Body, ct);
    }
}
