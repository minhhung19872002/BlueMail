using BlueMail.Application.Features.Security;
using BlueMail.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace BlueMail.Identity.Pages.Account;

/// <summary>
/// "Forgot password" (PROMPT 13): the organisation's administrators are told, and the page always answers the same,
/// so it cannot be used to find out which addresses exist. Rate limited per client IP.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(IdentityRateLimits.ForgotPassword)]
public sealed class ForgotPasswordModel(AccountNotices notices) : PageModel
{
    public static readonly TimeSpan MinAnswerTime = TimeSpan.FromMilliseconds(800);

    [BindProperty] public string Email { get; set; } = "";
    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Every answer takes at least this long, so the time does not tell a known address from an unknown one.
        var floor = Task.Delay(MinAnswerTime, ct);
        if (!string.IsNullOrWhiteSpace(Email) && Email.Length <= 320)
        {
            var ua = Request.Headers.UserAgent.ToString();
            await notices.RequestPasswordResetAsync(Email, HttpContext.Connection.RemoteIpAddress?.ToString(),
                ua.Length == 0 ? null : ua[..Math.Min(ua.Length, 512)], ct);
        }

        await floor;

        Sent = true;
        Email = "";
        return Page();
    }
}
