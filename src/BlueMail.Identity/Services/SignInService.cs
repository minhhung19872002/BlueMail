using System.Security.Claims;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Identity.Services;

public enum PasswordCheck { Ok, RequiresTwoFactor, Invalid, LockedOut, NotAllowed }

public sealed record PasswordCheckResult(PasswordCheck Outcome, User? User);

/// <summary>
/// Web sign-in for BlueID: password check with lockout, login events, and creation of the
/// identity.sessions row whose id travels in every token as bm_sid (PROMPT 8.1).
/// </summary>
public sealed class SignInService(
    BlueMailDbContext db,
    UserManager<User> users,
    SignInManager<User> signIn,
    ITenantContext tenant,
    AuditWriter audit,
    IHttpContextAccessor http,
    BlueMail.Application.Features.Admin.Policies.PolicyService policies)
{
    public async Task<PasswordCheckResult> CheckPasswordAsync(string email, string password, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(email);
        if (user is null)
        {
            // Burn the same time as a real check so response time does not reveal which addresses exist.
            Argon2PasswordHasher.Verify(password, Argon2PasswordHasher.Hash("timing-equaliser"));
            await RecordAsync(null, null, LoginResult.Failure, "unknown_user", ct);
            return new PasswordCheckResult(PasswordCheck.Invalid, null);
        }

        tenant.Set(user.TenantId, user.Id);
        var tenantStatus = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == user.TenantId).Select(t => t.Status).FirstAsync(ct);
        if (user.Status != UserStatus.Active || tenantStatus == TenantStatus.Suspended)
        {
            await RecordAsync(user.Id, user.TenantId, LoginResult.Failure, user.Status != UserStatus.Active ? "user_suspended" : "tenant_suspended", ct);
            return new PasswordCheckResult(PasswordCheck.NotAllowed, user);
        }

        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            await RecordAsync(user.Id, user.TenantId, LoginResult.Failure, "locked_out", ct);
            return new PasswordCheckResult(PasswordCheck.LockedOut, user);
        }

        if (!result.Succeeded)
        {
            // Lockout after the tenant's number of failures (policy lockout_attempts, PROMPT 7.4): 15 minutes.
            var unit = await policies.OrgUnitOfAsync(user.Id, ct);
            var limit = await policies.IntAsync(user.TenantId, unit, BlueMail.Application.Features.Admin.Policies.PolicyKeys.LockoutAttempts, ct);
            if (limit > 0 && await users.GetAccessFailedCountAsync(user) >= limit)
            {
                await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));
                await users.ResetAccessFailedCountAsync(user);
                await RecordAsync(user.Id, user.TenantId, LoginResult.Failure, "locked_out", ct);
                return new PasswordCheckResult(PasswordCheck.LockedOut, user);
            }

            await RecordAsync(user.Id, user.TenantId, LoginResult.Failure, "bad_password", ct);
            return new PasswordCheckResult(PasswordCheck.Invalid, user);
        }

        return new PasswordCheckResult(user.TwoFactorEnabled ? PasswordCheck.RequiresTwoFactor : PasswordCheck.Ok, user);
    }

    /// <summary>Creates the session and signs the browser in to BlueID with bm_sid in the cookie.</summary>
    public async Task CompleteAsync(User user, bool persistent, string method, CancellationToken ct)
    {
        tenant.Set(user.TenantId, user.Id);
        var ctx = http.HttpContext!;
        var session = new Session
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 512),
            Device = DeviceName(ctx.Request.Headers.UserAgent.ToString()),
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Sessions.Add(session);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await users.ResetAccessFailedCountAsync(user);
        await RecordAsync(user.Id, user.TenantId, LoginResult.Success, method, ct);
        await audit.WriteAsync(new AuditEntry("auth.login", AuditResult.Success, user.TenantId, user.Id, user.PrimaryEmail,
            session.Ip, session.UserAgent, "session", session.Id.ToString()), ct);
        await signIn.SignInWithClaimsAsync(user, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = persistent },
            [new Claim(OidcKeys.SessionClaim, session.Id.ToString())]);
    }

    /// <summary>Support sign-in (PROMPT 7.1): a session of the user that records who acts and ends after one hour.</summary>
    public async Task CompleteSupportAsync(User user, Guid actorUserId, CancellationToken ct)
    {
        tenant.Set(user.TenantId, user.Id);
        var ctx = http.HttpContext!;
        var session = new Session
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 512),
            Device = "support",
            LastSeenAt = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await RecordAsync(user.Id, user.TenantId, LoginResult.Success, "support", ct);
        await audit.WriteAsync(new AuditEntry("support.signin.use", AuditResult.Success, user.TenantId, actorUserId, null, session.Ip, session.UserAgent,
            "session", session.Id.ToString(), NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { target = user.PrimaryEmail })), ct);
        await signIn.SignInWithClaimsAsync(user, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = false, ExpiresUtc = session.ExpiresAt },
            [new Claim(OidcKeys.SessionClaim, session.Id.ToString())]);
    }

    public async Task SignOutAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (Guid.TryParse(principal.FindFirstValue(OidcKeys.SessionClaim), out var sid) &&
            Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var uid))
        {
            var now = DateTimeOffset.UtcNow;
            await db.Sessions.IgnoreQueryFilters().Where(s => s.Id == sid && s.UserId == uid && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        }

        await signIn.SignOutAsync();
    }

    private async Task RecordAsync(Guid? userId, Guid? tenantId, LoginResult result, string reason, CancellationToken ct)
    {
        var ctx = http.HttpContext!;
        var row = new LoginEvent
        {
            UserId = userId,
            Protocol = LoginProtocol.Web,
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 512),
            Result = result,
            Reason = reason,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        if (tenantId is { } t)
        {
            row.TenantId = t;
        }
        else
        {
            // Failed attempts on unknown addresses are kept under the platform tenant.
            row.TenantId = await db.Tenants.IgnoreQueryFilters().Where(x => x.Kind == TenantKind.Platform).Select(x => x.Id).FirstAsync(ct);
        }

        db.LoginEvents.Add(row);
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];

    /// <summary>Short device label from the User-Agent for the session list.</summary>
    internal static string? DeviceName(string? ua)
    {
        if (string.IsNullOrEmpty(ua))
        {
            return null;
        }

        string os = ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "";
        string browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "";
        var label = string.Join(" · ", new[] { browser, os }.Where(s => s.Length > 0));
        return label.Length == 0 ? null : label;
    }
}
