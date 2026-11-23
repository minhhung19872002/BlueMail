using BlueMail.Api.Auth;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Application.Web;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueMail.Api.Controllers;

/// <summary>App passwords for IMAP and SMTP submission (PROMPT 8.1).</summary>
[ApiController]
[Route("api/v1/me/app-passwords")]
public sealed class AppPasswordsController(ISender mediator) : ControllerBase
{
    private const string Own = "The caller's own app passwords (filtered by user id in the handlers).";

    [HttpGet]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<IReadOnlyList<AppPasswordDto>>> List(CancellationToken ct) => ApiResponses.Ok(await mediator.Send(new ListAppPasswordsQuery(), ct));

    [HttpPost]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<NewAppPasswordDto>> Create([FromBody] CreateAppPasswordCommand body, CancellationToken ct) => ApiResponses.Ok(await mediator.Send(body, ct));

    [HttpDelete("{id:guid}")]
    [SelfGuarded(Own)]
    public async Task<ApiResponse<bool>> Revoke(Guid id, CancellationToken ct)
    {
        await mediator.Send(new RevokeAppPasswordCommand(id), ct);
        return ApiResponses.Ok(true);
    }
}
