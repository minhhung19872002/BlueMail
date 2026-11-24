using System.Globalization;
using System.Security.Cryptography;
using BlueMail.Infrastructure.Security;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.DataProtection;

namespace BlueMail.Api.Auth;

/// <summary>
/// Access to the Hangfire dashboard (PROMPT 9: platform administrators only). The browser opens it without the API's
/// bearer token, so a platform administrator first asks the API for a short-lived, signed cookie bound to their session
/// (POST /api/v1/admin/platform/jobs-dashboard); every dashboard request checks the cookie and the session again
/// through <see cref="ISessionValidator"/> (signed out, suspended or demoted: no access).
/// </summary>
public sealed class JobsDashboard : IDashboardAsyncAuthorizationFilter
{
    public const string Path = "/hangfire";
    public const string CookieName = "bm_jobs";
    public const string Purpose = "BlueMail.JobsDashboard.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string Issue(IDataProtectionProvider dp, Guid userId, Guid sessionId, string stamp, DateTimeOffset now) =>
        dp.CreateProtector(Purpose).Protect(string.Join('|', userId.ToString("N"), sessionId.ToString("N"), stamp,
            (now + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var http = context.GetHttpContext();
        if (http.Request.Cookies[CookieName] is not { Length: > 0 } cookie)
        {
            return false;
        }

        string[] parts;
        try
        {
            parts = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(cookie).Split('|');
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var userId) || !Guid.TryParseExact(parts[1], "N", out var sessionId)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow)
        {
            return false;
        }

        var state = await http.RequestServices.GetRequiredService<ISessionValidator>().ValidateAsync(userId, sessionId, parts[2], http.RequestAborted);
        return state.IsValid && state.IsPlatformAdmin && state.ActorUserId is null; // not through a support sign-in
    }
}
