using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace BlueMail.Application.Features.MailAccess;

/// <summary>An authenticated mail-protocol login: who, and what to re-check while the connection stays open.</summary>
public sealed record MailLogin(Guid UserId, Guid TenantId, string Email, string SecurityStamp, Guid? AppPasswordId, Guid? SessionId);

public enum MailLoginFailure { None, Wrong, Locked, Disabled }

/// <summary>
/// Sign-in for IMAP and SMTP submission (PROMPT 8.1): account password (only without two-step verification), app
/// password (always allowed, required with two-step verification) or a BlueID access token (XOAUTH2/OAUTHBEARER).
/// Brute force is limited per account and per IP; every attempt goes to login_events. Whether an open connection may
/// continue is decided by <see cref="StillValidAsync"/> with the same rules as the web (one place, PROMPT 14.9).
/// </summary>
public sealed class MailLoginService(IServiceScopeFactory scopes, ISessionValidator sessions, BmSettings settings, OidcKeys keys, IConnectionMultiplexer? valkey = null)
{
    public const int MaxFailuresPerAccount = 10;
    public const int MaxFailuresPerIp = 30;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private static readonly Lazy<string> DummyHash = new(() => Argon2PasswordHasher.Hash("not-a-real-password"));

    public static string HashAppPassword(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(secret))));

    /// <summary>App passwords are shown in groups ("abcd efgh ijkl mnop"); spaces and case do not matter.</summary>
    private static string Normalize(string secret) => secret.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    public async Task<(MailLogin? Login, MailLoginFailure Failure)> AuthenticateAsync(string user, string secret, bool bearer, LoginProtocol protocol, IPAddress ip,
        CancellationToken ct)
    {
        var name = user.Trim().ToLowerInvariant();
        if (await LockedAsync(name, ip))
        {
            return (null, MailLoginFailure.Locked); // not even checked: no oracle for a password guesser
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var (login, reason) = bearer ? await BearerAsync(db, name, secret, ct) : await PasswordAsync(db, name, secret, ct);
        var target = login?.UserId ?? await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == name).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        var tenant = login?.TenantId ?? await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == name).Select(u => (Guid?)u.TenantId).FirstOrDefaultAsync(ct);
        if (tenant is { } t)
        {
            db.LoginEvents.Add(new LoginEvent
            {
                TenantId = t, UserId = target, Protocol = protocol, Ip = ip.ToString(), Result = login is null ? LoginResult.Failure : LoginResult.Success,
                Reason = login is null ? reason : (bearer ? "token" : login.AppPasswordId is null ? "password" : "app_password"), OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        if (login is null)
        {
            await CountFailureAsync(name, ip);
            return (null, reason == "disabled" ? MailLoginFailure.Disabled : MailLoginFailure.Wrong);
        }

        return (login, MailLoginFailure.None);
    }

    private static async Task<(MailLogin?, string)> PasswordAsync(BlueMailDbContext db, string name, string secret, CancellationToken ct)
    {
        var user = await FindAsync(db, name, ct);
        if (user is null)
        {
            Argon2PasswordHasher.Verify(secret, DummyHash.Value); // same cost for unknown users: no timing oracle
            return (null, "unknown_user");
        }

        if (await UsableAsync(db, user, ct) is { } why)
        {
            return (null, why);
        }

        var hash = HashAppPassword(secret);
        var app = await db.AppPasswords.IgnoreQueryFilters()
            .Where(a => a.UserId == user.Id && a.RevokedAt == null && a.DeletedAt == null && a.SecretHash == hash).FirstOrDefaultAsync(ct);
        if (app is not null)
        {
            app.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return (new MailLogin(user.Id, user.TenantId, user.PrimaryEmail, user.SecurityStamp ?? "", app.Id, null), "");
        }

        // The account password works only while two-step verification is off (PROMPT 8.1: app passwords are required then).
        if (user.TwoFactorEnabled)
        {
            return (null, "app_password_required");
        }

        return user.PasswordHash is { } phc && Argon2PasswordHasher.Verify(secret, phc)
            ? (new MailLogin(user.Id, user.TenantId, user.PrimaryEmail, user.SecurityStamp ?? "", null, null), "")
            : (null, "wrong_password");
    }

    private async Task<(MailLogin?, string)> BearerAsync(BlueMailDbContext db, string name, string token, CancellationToken ct)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = settings.IdentityUrl + "/",
            ValidAudience = OidcKeys.ApiAudience,
            IssuerSigningKey = keys.Signing,
            ValidTypes = ["at+jwt", "JWT"],
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst("sub")?.Value, out var userId)
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst(OidcKeys.SessionClaim)?.Value, out var sessionId)
            || result.ClaimsIdentity.FindFirst(OidcKeys.StampClaim)?.Value is not { } stamp)
        {
            return (null, "invalid_token");
        }

        var state = await sessions.ValidateAsync(userId, sessionId, stamp, ct);
        if (!state.IsValid)
        {
            return (null, "session_" + state.Rejection.ToString().ToLowerInvariant());
        }

        // The SASL user (if given) must be the token's user.
        if (name.Length > 0 && !name.Equals(state.PrimaryEmail, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "token_user_mismatch");
        }

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        return await UsableAsync(db, user, ct) is { } why ? (null, why) : (new MailLogin(user.Id, user.TenantId, user.PrimaryEmail, stamp, null, sessionId), "");
    }

    private static Task<User?> FindAsync(BlueMailDbContext db, string name, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.DeletedAt == null && (u.PrimaryEmail == name
            || db.UserAliases.IgnoreQueryFilters().Any(a => a.UserId == u.Id && a.Email == name && a.DeletedAt == null)), ct);

    /// <summary>Null when the account may use mail programs, otherwise why not.</summary>
    private static async Task<string?> UsableAsync(BlueMailDbContext db, User user, CancellationToken ct)
    {
        if (user.Status != UserStatus.Active || user.DeletedAt is not null)
        {
            return "disabled";
        }

        if (!user.ImapEnabled)
        {
            return "disabled"; // administrator turned off IMAP/SMTP for this user (PROMPT 7.3)
        }

        var tenant = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == user.TenantId).Select(t => t.Status).FirstAsync(ct);
        return tenant == TenantStatus.Active ? null : "disabled";
    }

    /// <summary>
    /// Whether an open IMAP/SMTP connection may go on: same checks as at sign-in (user, tenant, IMAP access, security
    /// stamp, app password not revoked, web session not revoked for token logins).
    /// </summary>
    public async Task<bool> StillValidAsync(MailLogin login, CancellationToken ct)
    {
        if (login.SessionId is { } sid)
        {
            if (!(await sessions.ValidateAsync(login.UserId, sid, login.SecurityStamp, ct)).IsValid)
            {
                return false;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == login.UserId, ct);
        if (user is null || await UsableAsync(db, user, ct) is not null || (login.SessionId is null && user.SecurityStamp != login.SecurityStamp))
        {
            return false;
        }

        return login.AppPasswordId is not { } app
               || await db.AppPasswords.IgnoreQueryFilters().AnyAsync(a => a.Id == app && a.RevokedAt == null && a.DeletedAt == null, ct);
    }

    /// <summary>Raised by user suspension, password change, app password revocation (the same Valkey channel as the web).</summary>
    public void OnUserInvalidated(Action<Guid> handler)
    {
        valkey?.GetSubscriber().Subscribe(RedisChannel.Literal(SessionValidator.Channel), (_, value) =>
        {
            if (Guid.TryParse(value.ToString(), out var id))
            {
                handler(id);
            }
        });
    }

    private async Task<bool> LockedAsync(string name, IPAddress ip)
    {
        if (valkey is null)
        {
            return false;
        }

        var db = valkey.GetDatabase();
        var a = await db.StringGetAsync("bm:authfail:u:" + name);
        var i = await db.StringGetAsync("bm:authfail:ip:" + ip);
        return (a.TryParse(out long na) && na >= MaxFailuresPerAccount) || (i.TryParse(out long ni) && ni >= MaxFailuresPerIp);
    }

    private async Task CountFailureAsync(string name, IPAddress ip)
    {
        if (valkey is null)
        {
            return;
        }

        var db = valkey.GetDatabase();
        foreach (var key in new[] { "bm:authfail:u:" + name, "bm:authfail:ip:" + ip })
        {
            if (await db.StringIncrementAsync(key) == 1)
            {
                await db.KeyExpireAsync(key, FailureWindow);
            }
        }
    }
}
