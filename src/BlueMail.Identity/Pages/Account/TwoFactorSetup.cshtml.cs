using BlueMail.Domain.Identity;
using BlueMail.Identity.Services;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BlueMail.Identity.Pages.Account;

[Authorize]
public sealed class TwoFactorSetupModel(UserManager<User> users, SignInManager<User> signIn, TwoFactorService twoFactor, BmSettings settings)
    : PageModel
{
    [BindProperty] public string ProtectedSecret { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public TwoFactorSetup? Setup { get; private set; }
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }
    public string? ErrorKey { get; private set; }
    public string ContinueUrl => settings.WebUrl + "/";

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Redirect("/account/login");
        }

        Setup = twoFactor.Begin(user, "BlueMail");
        ProtectedSecret = Setup.ProtectedSecret;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Redirect("/account/login");
        }

        var codes = await twoFactor.ConfirmAsync(user, ProtectedSecret, Code, ct);
        if (codes is null)
        {
            ErrorKey = "id.error.invalid_code";
            Setup = twoFactor.Begin(user, "BlueMail");
            ProtectedSecret = Setup.ProtectedSecret;
            Code = "";
            return Page();
        }

        // Enabling 2FA rotated the security stamp; re-issue this browser's cookie, keep its session id.
        var sid = User.FindFirst(OidcKeys.SessionClaim)?.Value;
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, sid is null ? [] : [new System.Security.Claims.Claim(OidcKeys.SessionClaim, sid)]);
        RecoveryCodes = codes.Select(TwoFactorService.Display).ToList();
        return Page();
    }
}
