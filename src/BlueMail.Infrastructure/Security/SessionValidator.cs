using System.Collections.Concurrent;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;

namespace BlueMail.Infrastructure.Security;

public enum SessionRejection { None, UnknownSession, SessionRevoked, UserNotActive, TenantSuspended, StampChanged }

public sealed record SessionState(Guid UserId, Guid TenantId, string PrimaryEmail, bool IsTenantAdmin, bool IsPlatformAdmin,
    string? Locale, string? TimeZone, string? TenantLocale, SessionRejection Rejection)
{
    public bool IsValid => Rejection == SessionRejection.None;
}

/// <summary>
/// The one place that answers "may the holder of this session still get in?" (PROMPT 8.1, 14.9):
/// used by the web API (token validation), and later by IMAP and SMTP submission. Results are cached
/// for a short time; <see cref="InvalidateUserAsync"/> drops the cache in every process at once
/// through a Valkey channel, so suspending a user takes effect immediately everywhere.
/// </summary>
public interface ISessionValidator
{
    Task<SessionState> ValidateAsync(Guid userId, Guid sessionId, string securityStamp, CancellationToken ct = default);
    Task InvalidateUserAsync(Guid userId);
}

public sealed class SessionValidator : ISessionValidator, IDisposable
{
    public const string Channel = "bm:invalidate-user";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    private readonly IConnectionMultiplexer? _valkey;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _userTokens = new();

    public SessionValidator(IServiceScopeFactory scopes, IMemoryCache cache, IConnectionMultiplexer? valkey = null)
    {
        _scopes = scopes;
        _cache = cache;
        _valkey = valkey;
        _valkey?.GetSubscriber().Subscribe(RedisChannel.Literal(Channel), (_, value) =>
        {
            if (Guid.TryParse(value.ToString(), out var id))
            {
                DropLocal(id);
            }
        });
    }

    public async Task<SessionState> ValidateAsync(Guid userId, Guid sessionId, string securityStamp, CancellationToken ct = default)
    {
        var key = (userId, sessionId, securityStamp);
        if (_cache.TryGetValue(key, out SessionState? cached) && cached is not null)
        {
            return cached;
        }

        var token = _userTokens.GetOrAdd(userId, _ => new CancellationTokenSource());
        var state = await LoadAsync(userId, sessionId, securityStamp, ct);
        _cache.Set(key, state, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(CacheFor)
            .AddExpirationToken(new CancellationChangeToken(token.Token)));
        return state;
    }

    private async Task<SessionState> LoadAsync(Guid userId, Guid sessionId, string stamp, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();

        // Platform-level lookup by id: the tenant is not known yet, so the tenant filter is bypassed here only.
        var row = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.TenantId, u.PrimaryEmail, u.Status, u.DeletedAt, u.SecurityStamp, u.IsTenantAdmin, u.IsPlatformAdmin,
                u.Locale, u.TimeZone,
                TenantStatus = db.Tenants.IgnoreQueryFilters().Where(t => t.Id == u.TenantId).Select(t => (TenantStatus?)t.Status).FirstOrDefault(),
                TenantLocale = db.Tenants.IgnoreQueryFilters().Where(t => t.Id == u.TenantId).Select(t => t.DefaultLocale).FirstOrDefault(),
                Session = db.Sessions.IgnoreQueryFilters().Where(s => s.Id == sessionId && s.UserId == u.Id)
                    .Select(s => new { s.RevokedAt }).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return new SessionState(userId, Guid.Empty, "", false, false, null, null, null, SessionRejection.UserNotActive);
        }

        var rejection =
            row.Status != UserStatus.Active || row.DeletedAt is not null ? SessionRejection.UserNotActive :
            row.TenantStatus is null or TenantStatus.Suspended ? SessionRejection.TenantSuspended :
            row.Session is null ? SessionRejection.UnknownSession :
            row.Session.RevokedAt is not null ? SessionRejection.SessionRevoked :
            !string.Equals(row.SecurityStamp, stamp, StringComparison.Ordinal) ? SessionRejection.StampChanged :
            SessionRejection.None;

        return new SessionState(userId, row.TenantId, row.PrimaryEmail, row.IsTenantAdmin, row.IsPlatformAdmin,
            row.Locale, row.TimeZone, row.TenantLocale, rejection);
    }

    public async Task InvalidateUserAsync(Guid userId)
    {
        DropLocal(userId);
        if (_valkey is not null)
        {
            await _valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), userId.ToString("D"));
        }
    }

    private void DropLocal(Guid userId)
    {
        if (_userTokens.TryRemove(userId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var cts in _userTokens.Values)
        {
            cts.Dispose();
        }
    }
}
