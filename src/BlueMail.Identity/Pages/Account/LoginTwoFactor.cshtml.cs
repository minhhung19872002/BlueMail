using System.Security.Claims;
using BlueMail.Domain.Identity;
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
public sealed class LoginTwoFactorModel(UserManager<User> users, TwoFactorService twoFactor, SignInService signIn) : PageModel
{
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public bool IsRecoveryCode { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorKey { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await PendingAsync() is null ? Redirect("/account/login") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var pending = await PendingAsync();
        if (pending is null)
        {
            return Redirect("/account/login");
        }

        var (user, remember) = pending.Value;
        if (await users.IsLockedOutAsync(user))
        {
            ErrorKey = "id.error.locked";
            return Page();
        }

        var ok = IsRecoveryCode
            ? await twoFactor.VerifyRecoveryCodeAsync(user, Code, ct)
            : await twoFactor.VerifyAsync(user, Code, ct);
        if (!ok)
        {
            await users.AccessFailedAsync(user); // wrong codes count toward lockout like wrong passwords
            ErrorKey = "id.error.invalid_code";
            Code = "";
            return Page();
        }

        await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await signIn.CompleteAsync(user, remember, IsRecoveryCode ? "recovery_code" : "totp", ct);
        var target = !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
        return user.MustChangePassword ? Redirect("/account/change-password?returnUrl=" + Uri.EscapeDataString(target)) : LocalRedirect(target);
    }

    private async Task<(User User, bool Remember)?> PendingAsync()
    {
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        var id = result.Principal?.FindFirstValue(ClaimTypes.Name);
        if (id is null || await users.FindByIdAsync(id) is not { } user)
        {
            return null;
        }

        return (user, result.Principal!.FindFirstValue("remember") == "1");
    }
}
