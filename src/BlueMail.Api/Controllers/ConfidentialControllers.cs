using BlueMail.Api.Auth;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Sending;
using BlueMail.Application.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>
/// The page a recipient of a confidential message opens (Phase 12). No account: the personal link token is the
/// credential, plus a mailed one-time code when the sender asked for it.
/// </summary>
[ApiController]
[Route("api/v1/confidential/{token}")]
public sealed class ConfidentialViewController(ConfidentialService confidential) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ApiResponse<ConfidentialMetaDto>> Meta(string token, CancellationToken ct) => ApiResponses.Ok(await confidential.MetaAsync(token, ct));

    [HttpPost("code")]
    [AllowAnonymous]
    public async Task<ApiResponse<bool>> Code(string token, CancellationToken ct)
    {
        await confidential.SendCodeAsync(token, ct);
        return ApiResponses.Ok(true);
    }

    public sealed record OpenBody(string? Code);

    [HttpPost("open")]
    [AllowAnonymous]
    public async Task<ApiResponse<ConfidentialContentDto>> Open(string token, [FromBody] OpenBody body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return ApiResponses.Ok(await confidential.OpenAsync(token, body.Code, ct));
    }

    [HttpGet("files/{part}")]
    [AllowAnonymous]
    public async Task<IActionResult> File(string token, string part, [FromQuery] string e, [FromQuery] string s, CancellationToken ct)
    {
        var (name, _, content) = await confidential.FileAsync(token, part, e, s, ct);
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "no-store";
        return File(content, "application/octet-stream", name);
    }
}

/// <summary>The sender's view of a confidential message they sent: who opened it, withdraw or restore access.</summary>
[ApiController]
[Route("api/v1/mail/confidential")]
public sealed class ConfidentialSenderController(ConfidentialService confidential, ICurrentUser me) : ControllerBase
{
    private const string Own = "Only the sender's own confidential messages (tenant and sender user id checked in the service).";

    [HttpGet]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<ConfidentialStatusDto?>> Status([FromQuery] string rfcMessageId, CancellationToken ct) =>
        ApiResponses.Ok(await confidential.StatusAsync(me.TenantId, me.UserId, rfcMessageId, ct));

    public sealed record RevokeBody(bool Revoked);

    [HttpPut("{id:guid}/revoked")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> Revoke(Guid id, [FromBody] RevokeBody body, CancellationToken ct)
    {
        await confidential.RevokeAsync(me.TenantId, me.UserId, id, body.Revoked, ct);
        return ApiResponses.Ok(true);
    }
}
