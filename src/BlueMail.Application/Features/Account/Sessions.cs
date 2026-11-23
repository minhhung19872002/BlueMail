using BlueMail.Application.Common;
using BlueMail.Domain.Audit;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Account;

public sealed record SessionDto(Guid Id, string? Device, string? Ip, string? UserAgent, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool IsCurrent);

public sealed record ListMySessionsQuery : IRequest<IReadOnlyList<SessionDto>>;

public sealed class ListMySessionsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListMySessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<IReadOnlyList<SessionDto>> Handle(ListMySessionsQuery q, CancellationToken ct) =>
        await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == me.UserId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt).ThenBy(s => s.Id)
            .Select(s => new SessionDto(s.Id, s.Device, s.Ip, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.Id == me.SessionId))
            .ToListAsync(ct);
}

/// <summary>Sign out another device (or this one). Takes effect immediately through the session validator.</summary>
public sealed record RevokeSessionCommand(Guid SessionId) : IRequest;

public sealed class RevokeSessionHandler(BlueMailDbContext db, ISessionValidator sessions, AuditWriter audit, ICurrentUser me)
    : IRequestHandler<RevokeSessionCommand>
{
    public async Task Handle(RevokeSessionCommand c, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await db.Sessions
            .Where(s => s.Id == c.SessionId && s.UserId == me.UserId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        if (changed == 0)
        {
            throw AppException.NotFound("session_not_found");
        }

        await audit.WriteAsync(new AuditEntry("session.revoke", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent,
            "session", c.SessionId.ToString()), ct);
        await sessions.InvalidateUserAsync(me.UserId);
    }
}
