using System.Security.Claims;
using BlueMail.Identity.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace BlueMail.Identity.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(IdentityRateLimits.Login)]
public sealed class LoginModel(SignInService signIn, TenantLinks links) : PageModel
{
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool Remember { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public string? Hint { get; set; }
    public string? ErrorKey { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Email = Hint ?? "";
        await links.ApplyAsync(ViewData, Email, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await links.ApplyAsync(ViewData, Email, ct);
        var check = await signIn.CheckPasswordAsync(Email.Trim(), Password, ct);
        switch (check.Outcome)
        {
            case PasswordCheck.Ok:
                await signIn.CompleteAsync(check.User!, Remember, "password", ct);
                return check.User!.MustChangePassword
                    ? Redirect("/account/change-password?returnUrl=" + Uri.EscapeDataString(SafeReturn()))
                    : LocalRedirect(SafeReturn());
            case PasswordCheck.RequiresTwoFactor:
                var pending = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
                pending.AddClaim(new Claim(ClaimTypes.Name, check.User!.Id.ToString()));
                pending.AddClaim(new Claim("remember", Remember ? "1" : "0"));
                await HttpContext.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(pending));
                return Redirect("/account/login-2fa?returnUrl=" + Uri.EscapeDataString(SafeReturn()));
            case PasswordCheck.LockedOut:
                ErrorKey = "id.error.locked";
                break;
            case PasswordCheck.NotAllowed:
                ErrorKey = "id.error.suspended";
                break;
            default:
                ErrorKey = "id.error.invalid_credentials";
                break;
        }

        Password = "";
        return Page();
    }

    private string SafeReturn() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
