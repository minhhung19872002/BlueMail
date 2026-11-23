using System.Globalization;
using System.Text;
using BlueMail.Api.Auth;
using BlueMail.Application.Features.Sending;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

[ApiController]
[Route("api/v1/mail")]
public sealed class ComposeController(ISender mediator) : ControllerBase
{
    private const string Guarded = "Mailbox access is checked by IMailStore.OpenAsync (own mailbox or granted shared mailbox).";

    [HttpGet("compose/from-options")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<IReadOnlyList<SenderPolicy.FromOption>>> FromOptions([FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new FromOptionsQuery(mailboxId), ct));

    [HttpGet("compose/from-message/{messageId:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<DraftState>> FromMessage(Guid messageId, [FromQuery] string mode, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new ComposeFromMessageQuery(mailboxId, messageId, mode), ct));

    [HttpGet("compose/suggest")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<IReadOnlyList<SuggestedAddress>>> Suggest([FromQuery] string q, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new SuggestRecipientsQuery(mailboxId, q ?? ""), ct));

    public sealed record SaveDraftBody(Guid? DraftId, int? Version, DraftState State);

    [HttpPost("drafts")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<DraftService.DraftDto>> SaveDraft([FromQuery] Guid? mailboxId, [FromBody] SaveDraftBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new SaveDraftCommand(mailboxId, body.DraftId, body.Version, body.State), ct));

    [HttpGet("drafts/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<DraftService.DraftDto>> GetDraft(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetDraftQuery(mailboxId, id, null), ct));

    [HttpGet("drafts/by-message/{messageId:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<DraftService.DraftDto>> DraftByMessage(Guid messageId, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new GetDraftQuery(mailboxId, null, messageId), ct));

    [HttpDelete("drafts/{id:guid}")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<bool>> DiscardDraft(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct)
    {
        await mediator.Send(new DiscardDraftCommand(mailboxId, id), ct);
        return ApiResponses.Ok(true);
    }

    public sealed record SendBody(DateTimeOffset? ScheduleAt, bool Archive, int UndoSeconds = 5);

    [HttpPost("drafts/{id:guid}/send")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<SendService.SendResult>> Send(Guid id, [FromQuery] Guid? mailboxId, [FromBody] SendBody body, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new SendDraftCommand(mailboxId, id, body.ScheduleAt, body.Archive, body.UndoSeconds), ct));

    [HttpPost("outgoing/{id:guid}/cancel")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<Guid>> Cancel(Guid id, [FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new CancelSendCommand(mailboxId, id), ct));

    [HttpGet("outgoing")]
    [SelfGuarded(Guarded)]
    public async Task<ApiResponse<IReadOnlyList<OutgoingDto>>> Pending([FromQuery] Guid? mailboxId, CancellationToken ct) =>
        ApiResponses.Ok(await mediator.Send(new PendingSendsQuery(mailboxId), ct));

    [HttpGet("signatures")]
    [SelfGuarded("Own signatures only (filtered by user id).")]
    public async Task<ApiResponse<IReadOnlyList<SignatureDto>>> Signatures(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListSignaturesQuery(), ct));

    [HttpPost("signatures")]
    [SelfGuarded("Own signatures only (filtered by user id).")]
    public async Task<ApiResponse<Guid>> SaveSignature([FromBody] SaveSignatureCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("signatures/{id:guid}")]
    [SelfGuarded("Own signatures only (filtered by user id).")]
    public async Task<ApiResponse<bool>> DeleteSignature(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteSignatureCommand(id), ct);
        return ApiResponses.Ok(true);
    }

    [HttpGet("templates")]
    [SelfGuarded("Own templates only (filtered by user id).")]
    public async Task<ApiResponse<IReadOnlyList<TemplateDto>>> Templates(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListTemplatesQuery(), ct));

    [HttpPost("templates")]
    [SelfGuarded("Own templates only (filtered by user id).")]
    public async Task<ApiResponse<Guid>> SaveTemplate([FromBody] SaveTemplateCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("templates/{id:guid}")]
    [SelfGuarded("Own templates only (filtered by user id).")]
    public async Task<ApiResponse<bool>> DeleteTemplate(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteTemplateCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}

/// <summary>
/// tus 1.0.0 core protocol (creation, HEAD, PATCH) for compose attachments (PROMPT 6.4). Headers per
/// https://tus.io/protocols/resumable-upload: Tus-Resumable, Upload-Length, Upload-Offset, Upload-Metadata.
/// </summary>
[ApiController]
[Route("api/v1/uploads")]
public sealed class UploadsController(UploadService uploads) : ControllerBase
{
    private const string Self = "Uploads belong to the caller (filtered by user id).";

    private void Tus() => Response.Headers["Tus-Resumable"] = "1.0.0";

    [HttpOptions]
    [AllowAnonymous]
    public IActionResult Options()
    {
        Tus();
        Response.Headers["Tus-Version"] = "1.0.0";
        Response.Headers["Tus-Extension"] = "creation,termination";
        Response.Headers["Tus-Max-Size"] = UploadService.MaxFileBytes.ToString(CultureInfo.InvariantCulture);
        return NoContent();
    }

    [HttpPost]
    [SelfGuarded(Self)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        Tus();
        if (!long.TryParse(Request.Headers["Upload-Length"], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return BadRequest();
        }

        var meta = ParseMetadata(Request.Headers["Upload-Metadata"].ToString());
        var info = await uploads.CreateAsync(meta.GetValueOrDefault("filename") ?? "file", meta.GetValueOrDefault("filetype"), length, ct);
        Response.Headers.Location = Url.Content("~/api/v1/uploads/" + info.Id.ToString("N"));
        Response.Headers["Upload-Offset"] = info.Offset.ToString(CultureInfo.InvariantCulture);
        return StatusCode(201, ApiResponses.Ok(info));
    }

    [HttpHead("{id:guid}")]
    [SelfGuarded(Self)]
    public async Task<IActionResult> Head(Guid id, CancellationToken ct)
    {
        Tus();
        var info = await uploads.GetAsync(id, ct);
        Response.Headers["Upload-Offset"] = info.Offset.ToString(CultureInfo.InvariantCulture);
        Response.Headers["Upload-Length"] = info.Length.ToString(CultureInfo.InvariantCulture);
        Response.Headers.CacheControl = "no-store";
        return Ok();
    }

    [HttpGet("{id:guid}")]
    [SelfGuarded(Self)]
    public async Task<ApiResponse<UploadInfo>> Get(Guid id, CancellationToken ct) => ApiResponses.Ok(await uploads.GetAsync(id, ct));

    [HttpPatch("{id:guid}")]
    [SelfGuarded(Self)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> Patch(Guid id, CancellationToken ct)
    {
        Tus();
        if (Request.ContentType != "application/offset+octet-stream")
        {
            return StatusCode(415);
        }

        if (!long.TryParse(Request.Headers["Upload-Offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return BadRequest();
        }

        var info = await uploads.AppendAsync(id, offset, Request.Body, Request.ContentLength, ct);
        Response.Headers["Upload-Offset"] = info.Offset.ToString(CultureInfo.InvariantCulture);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [SelfGuarded(Self)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        Tus();
        await uploads.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>The uploaded bytes, for the editor's preview of inline images (bearer token: fetched by the app).</summary>
    [HttpGet("{id:guid}/content")]
    [SelfGuarded(Self)]
    public async Task<IActionResult> Content(Guid id, CancellationToken ct)
    {
        var (info, content) = await uploads.ReadAsync(id, ct);
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        var safe = info.ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" ? info.ContentType : "application/octet-stream";
        return File(content, safe);
    }

    /// <summary>tus Upload-Metadata: comma-separated "key base64(value)" pairs.</summary>
    public static Dictionary<string, string> ParseMetadata(string header)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sp = pair.IndexOf(' ');
            var key = sp < 0 ? pair : pair[..sp];
            try
            {
                d[key] = sp < 0 ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(pair[(sp + 1)..]));
            }
            catch (FormatException)
            {
                // ignore malformed values
            }
        }

        return d;
    }
}

/// <summary>Public download of large files sent as links; the random token in the URL is the credential.</summary>
[ApiController]
[Route("api/v1/files")]
public sealed class LargeFilesController(LargeFiles files) : ControllerBase
{
    [HttpGet("{token}")]
    [AllowAnonymous]
    public async Task Download(string token, CancellationToken ct)
    {
        var f = await files.OpenAsync(token, ct);
        if (f is null)
        {
            await Problems.WriteAsync(HttpContext, System.Net.HttpStatusCode.NotFound, "link_expired");
            return;
        }

        Response.ContentType = "application/octet-stream";
        Response.ContentLength = f.Size;
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = f.FileName }.ToString();
        await f.WriteTo(Response.Body, ct);
    }
}
