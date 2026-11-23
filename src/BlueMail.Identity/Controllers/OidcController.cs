using System.Security.Claims;
using BlueMail.Domain.Identity;
using BlueMail.Identity.Services;
using BlueMail.Infrastructure.Security;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace BlueMail.Identity.Controllers;

/// <summary>OIDC endpoints of BlueID (OpenIddict pass-through mode).</summary>
public sealed class OidcController(UserManager<User> users, ISessionValidator sessions, SignInService signIn) : Controller
{
    [AllowAnonymous]
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        if (!cookie.Succeeded || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Error(Errors.LoginRequired, "The user is not signed in."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            var returnUrl = Request.PathBase + Request.Path + QueryString.Create(
                Request.HasFormContentType ? Request.Form.ToList() : Request.Query.Where(q => q.Key != Parameters.Prompt).ToList());
            var login = "/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
            if (!string.IsNullOrEmpty(request.LoginHint))
            {
                login += "&hint=" + Uri.EscapeDataString(request.LoginHint);
            }

            return Redirect(login);
        }

        var state = await ValidateCookieSessionAsync(cookie.Principal!);
        if (state is null)
        {
            await signIn.SignOutAsync(cookie.Principal!, HttpContext.RequestAborted);
            return Redirect("/account/login?returnUrl=" + Uri.EscapeDataString(Request.PathBase + Request.Path + Request.QueryString));
        }

        var user = await users.FindByIdAsync(state.Value.UserId.ToString());
        if (user is null)
        {
            return Forbid(Error(Errors.LoginRequired, "Unknown user."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (user.MustChangePassword)
        {
            return Redirect("/account/change-password?returnUrl=" + Uri.EscapeDataString(Request.PathBase + Request.Path + Request.QueryString));
        }

        var principal = BuildPrincipal(user, state.Value.SessionId, request.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Token()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Forbid(Error(Errors.UnsupportedGrantType, "Grant type not supported."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var p = result.Principal!;
        // Every refresh re-asks the session validator: a suspended user or revoked session gets no new token.
        if (!Guid.TryParse(p.GetClaim(Claims.Subject), out var userId) ||
            !Guid.TryParse(p.GetClaim(OidcKeys.SessionClaim), out var sessionId) ||
            p.GetClaim(OidcKeys.StampClaim) is not { } stamp)
        {
            return Forbid(Error(Errors.InvalidGrant, "Token is incomplete."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var state = await sessions.ValidateAsync(userId, sessionId, stamp, HttpContext.RequestAborted);
        if (!state.IsValid)
        {
            return Forbid(Error(Errors.InvalidGrant, "The session is no longer valid."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Forbid(Error(Errors.InvalidGrant, "Unknown user."), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return SignIn(BuildPrincipal(user, sessionId, p.GetScopes()), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Logout()
    {
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (cookie.Succeeded)
        {
            await signIn.SignOutAsync(cookie.Principal!, HttpContext.RequestAborted);
        }

        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> UserInfo()
    {
        var user = await users.FindByIdAsync(User.GetClaim(Claims.Subject) ?? "");
        if (user is null)
        {
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Ok(new Dictionary<string, object?>
        {
            [Claims.Subject] = user.Id.ToString(),
            [Claims.Email] = user.PrimaryEmail,
            [Claims.EmailVerified] = true,
            [Claims.Name] = user.DisplayName,
            [Claims.Locale] = user.Locale,
            [Claims.Zoneinfo] = user.TimeZone,
        });
    }

    private async Task<(Guid UserId, Guid SessionId)?> ValidateCookieSessionAsync(ClaimsPrincipal cookie)
    {
        if (!Guid.TryParse(cookie.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ||
            !Guid.TryParse(cookie.FindFirstValue(OidcKeys.SessionClaim), out var sid))
        {
            return null;
        }

        var stamp = cookie.FindFirstValue("AspNet.Identity.SecurityStamp") ?? "";
        var state = await sessions.ValidateAsync(uid, sid, stamp, HttpContext.RequestAborted);
        return state.IsValid ? (uid, sid) : null;
    }

    private static ClaimsPrincipal BuildPrincipal(User user, Guid sessionId, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.PrimaryEmail)
            .SetClaim(Claims.Name, user.DisplayName)
            .SetClaim(OidcKeys.SessionClaim, sessionId.ToString())
            .SetClaim(OidcKeys.StampClaim, user.SecurityStamp)
            .SetClaim(OidcKeys.TenantClaim, user.TenantId.ToString());
        identity.SetScopes(scopes);
        identity.SetResources(OidcKeys.ApiAudience);
        identity.SetDestinations(c => c.Type switch
        {
            Claims.Name or Claims.Email => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken],
        });
        return new ClaimsPrincipal(identity);
    }

    private static AuthenticationProperties Error(string error, string description) => new(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
    });
}
