using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Security;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BlueMail.Identity.Pages.Account;

[Authorize]
public sealed class ChangePasswordModel(UserManager<User> users, SignInManager<User> signIn, ISessionValidator sessions,
    ITenantContext tenant, AuditWriter audit) : PageModel
{
    [BindProperty] public string Current { get; set; } = "";
    [BindProperty] public string New { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public bool Forced { get; private set; }
    public List<string> ErrorKeys { get; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Redirect("/account/login");
        }

        Forced = user.MustChangePassword;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Redirect("/account/login");
        }

        tenant.Set(user.TenantId, user.Id);
        Forced = user.MustChangePassword;
        if (New != Confirm)
        {
            ErrorKeys.Add("id.error.password_mismatch");
            return Page();
        }

        if (New == Current)
        {
            ErrorKeys.Add("id.error.password_same");
            return Page();
        }

        var result = await users.ChangePasswordAsync(user, Current, New); // rotates the security stamp
        if (!result.Succeeded)
        {
            ErrorKeys.AddRange(result.Errors.Select(e => e.Code == "PasswordMismatch" ? "id.error.invalid_credentials" : "id.error.password_weak").Distinct());
            return Page();
        }

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await audit.WriteAsync(new AuditEntry("auth.password.change", AuditResult.Success, user.TenantId, user.Id, user.PrimaryEmail,
            HttpContext.Connection.RemoteIpAddress?.ToString()), ct);

        // Other sessions carry the old stamp and are now rejected; refresh this browser's cookie with the new one.
        await sessions.InvalidateUserAsync(user.Id);
        var sid = User.FindFirst(OidcKeys.SessionClaim)?.Value;
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, sid is null ? [] : [new System.Security.Claims.Claim(OidcKeys.SessionClaim, sid)]);
        return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }
}
