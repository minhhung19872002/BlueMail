using System.Net;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Operations;

// ---------- IP pools (platform, PROMPT 2.4) ----------

public sealed record SendingIpDto(int Id, string Address, string Helo, bool Enabled, bool? PtrOk, int? WarmupDay, int? CapToday, int SentToday);

public sealed record IpPoolDto(Guid Id, string Name, string Kind, bool IsDefault, int Tenants, IReadOnlyList<SendingIpDto> Ips);

public sealed record ListIpPoolsQuery : IRequest<IReadOnlyList<IpPoolDto>>;

public sealed class ListIpPoolsHandler(BlueMailDbContext db) : IRequestHandler<ListIpPoolsQuery, IReadOnlyList<IpPoolDto>>
{
    public async Task<IReadOnlyList<IpPoolDto>> Handle(ListIpPoolsQuery q, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var pools = await db.IpPools.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var ips = await db.SendingIps.AsNoTracking().OrderBy(i => i.Address).ToListAsync(ct);
        var sent = await db.SendingIpDays.AsNoTracking().Where(d => d.Day == today).ToDictionaryAsync(d => d.SendingIpId, d => d.Sent, ct);
        var tenants = await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.DeletedAt == null).GroupBy(t => t.IpPoolId)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return pools.Select(p => new IpPoolDto(p.Id, p.Name, EnumNames.ToUpperSnake(p.Kind.ToString()), p.IsDefault,
            tenants.Where(t => t.Key == p.Id || (p.IsDefault && t.Key == null)).Sum(t => t.Count),
            ips.Where(i => i.PoolId == p.Id).Select(i => new SendingIpDto(i.Id, i.Address, i.Helo, i.Enabled, i.PtrOk,
                i.WarmupStartedAt is { } w ? (int)(now.UtcDateTime.Date - w.UtcDateTime.Date).TotalDays + 1 : null,
                WarmupSchedule.CapOn(i.WarmupStartedAt, now), sent.GetValueOrDefault(i.Id))).ToList())).ToList();
    }
}

public sealed record SaveIpPoolCommand(Guid? Id, string Name, IpPoolKind Kind, bool IsDefault) : IRequest<Guid>;

public sealed class SaveIpPoolHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SaveIpPoolCommand, Guid>
{
    public async Task<Guid> Handle(SaveIpPoolCommand c, CancellationToken ct)
    {
        var name = c.Name.Trim();
        if (name.Length is 0 or > 100)
        {
            throw AppException.BadRequest("ip_pool_name");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pool = c.Id is { } id ? await db.IpPools.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound() : new IpPool { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        if (c.Id is null)
        {
            db.IpPools.Add(pool);
        }

        if (c.IsDefault && !pool.IsDefault)
        {
            // Exactly one default pool (unique index): the previous one stops being default in the same transaction.
            await db.IpPools.Where(p => p.IsDefault && p.Id != pool.Id).ExecuteUpdateAsync(p => p.SetProperty(x => x.IsDefault, false), ct);
        }

        pool.Name = name;
        pool.Kind = c.Kind;
        pool.IsDefault = c.IsDefault;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw AppException.Conflict("ip_pool_exists");
        }

        await audit.WriteAsync(new AuditEntry("ip_pool.save", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "ip_pool", pool.Id.ToString(),
            NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { name, c.Kind, c.IsDefault })), ct);
        await tx.CommitAsync(ct);
        return pool.Id;
    }
}

public sealed record SaveSendingIpCommand(int? Id, Guid PoolId, string Address, string Helo, bool Enabled, bool StartWarmup) : IRequest<int>;

public sealed class SaveSendingIpHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SaveSendingIpCommand, int>
{
    public async Task<int> Handle(SaveSendingIpCommand c, CancellationToken ct)
    {
        if (!IPAddress.TryParse(c.Address.Trim(), out var address))
        {
            throw AppException.BadRequest("ip_address_invalid");
        }

        var helo = c.Helo.Trim().TrimEnd('.').ToLowerInvariant();
        if (helo.Length is 0 or > 253 || !helo.Contains('.', StringComparison.Ordinal) || Uri.CheckHostName(helo) != UriHostNameType.Dns)
        {
            throw AppException.BadRequest("helo_invalid");
        }

        if (!await db.IpPools.AnyAsync(p => p.Id == c.PoolId, ct))
        {
            throw AppException.NotFound("ip_pool_not_found");
        }

        var ip = c.Id is { } id ? await db.SendingIps.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw AppException.NotFound() : new SendingIp();
        if (c.Id is null)
        {
            db.SendingIps.Add(ip);
        }

        var changed = ip.Address != address.ToString() || ip.Helo != helo;
        ip.PoolId = c.PoolId;
        ip.Address = address.ToString();
        ip.Helo = helo;
        ip.Enabled = c.Enabled;
        if (changed)
        {
            ip.PtrOk = null; // checked again before use
            ip.PtrCheckedAt = null;
        }

        if (c.StartWarmup)
        {
            ip.WarmupStartedAt = DateTimeOffset.UtcNow;
        }
        else if (c.Id is null)
        {
            ip.WarmupStartedAt = null;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw AppException.Conflict("ip_address_exists");
        }

        await audit.WriteAsync(new AuditEntry("sending_ip.save", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "sending_ip", ip.Address,
            NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { ip.PoolId, helo, c.Enabled, c.StartWarmup })), ct);
        return ip.Id;
    }
}

public sealed record AssignTenantPoolCommand(Guid TenantId, Guid? PoolId) : IRequest<Unit>;

public sealed class AssignTenantPoolHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<AssignTenantPoolCommand, Unit>
{
    public async Task<Unit> Handle(AssignTenantPoolCommand c, CancellationToken ct)
    {
        if (c.PoolId is { } pool && !await db.IpPools.AnyAsync(p => p.Id == pool, ct))
        {
            throw AppException.NotFound("ip_pool_not_found");
        }

        var n = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == c.TenantId && t.DeletedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.IpPoolId, c.PoolId), ct);
        if (n == 0)
        {
            throw AppException.NotFound();
        }

        await audit.WriteAsync(new AuditEntry("tenant.ip_pool", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "tenant", c.TenantId.ToString(),
            NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { c.PoolId })), ct);
        return Unit.Value;
    }
}

// ---------- TLS reporting (platform) ----------

public sealed record TlsSummaryDto(string Domain, string PolicyType, int Successful, int Failed, IReadOnlyList<string> FailureTypes);

public sealed record TlsSummaryQuery(int Days) : IRequest<IReadOnlyList<TlsSummaryDto>>;

public sealed class TlsSummaryHandler(BlueMailDbContext db) : IRequestHandler<TlsSummaryQuery, IReadOnlyList<TlsSummaryDto>>
{
    public async Task<IReadOnlyList<TlsSummaryDto>> Handle(TlsSummaryQuery q, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Clamp(q.Days, 1, 90)));
        var rows = await db.TlsResults.AsNoTracking().Where(r => r.Day >= since).ToListAsync(ct);
        return rows.GroupBy(r => (r.PolicyDomain, r.PolicyType))
            .Select(g => new TlsSummaryDto(g.Key.PolicyDomain, g.Key.PolicyType, g.Where(r => r.FailureType == null).Sum(r => r.Count),
                g.Where(r => r.FailureType != null).Sum(r => r.Count), g.Where(r => r.FailureType != null).Select(r => r.FailureType!).Distinct().Order().ToList()))
            .OrderByDescending(x => x.Failed).ThenByDescending(x => x.Successful).Take(200).ToList();
    }
}

// ---------- sending self-lock (tenant administrators) ----------

public sealed record SendingLockDto(Guid UserId, string Email, string DisplayName, DateTimeOffset LockedAt, string? Reason, int Sent, int HardBounces, int Complaints);

public sealed record ListSendingLocksQuery : IRequest<IReadOnlyList<SendingLockDto>>;

public sealed class ListSendingLocksHandler(BlueMailDbContext db, ICurrentUser me, SenderReputation reputation) : IRequestHandler<ListSendingLocksQuery, IReadOnlyList<SendingLockDto>>
{
    public async Task<IReadOnlyList<SendingLockDto>> Handle(ListSendingLocksQuery q, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.TenantId == me.TenantId && u.SendingLockedAt != null && u.DeletedAt == null)
            .OrderByDescending(u => u.SendingLockedAt).Take(200).ToListAsync(ct);
        var result = new List<SendingLockDto>();
        foreach (var u in users)
        {
            var s = await reputation.StatsAsync(u.Id, ct);
            result.Add(new SendingLockDto(u.Id, u.PrimaryEmail, u.DisplayName, u.SendingLockedAt!.Value, u.SendingLockReason, s.Sent, s.HardBounces, s.Complaints));
        }

        return result;
    }
}

public sealed record UnlockSendingCommand(Guid UserId) : IRequest<Unit>;

public sealed class UnlockSendingHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<UnlockSendingCommand, Unit>
{
    public async Task<Unit> Handle(UnlockSendingCommand c, CancellationToken ct)
    {
        var n = await db.Users.Where(u => u.Id == c.UserId && u.TenantId == me.TenantId && u.SendingLockedAt != null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SendingLockedAt, (DateTimeOffset?)null).SetProperty(x => x.SendingLockReason, (string?)null), ct);
        if (n == 0)
        {
            throw AppException.NotFound();
        }

        await audit.WriteAsync(new AuditEntry("sending.unlock", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", c.UserId.ToString()), ct);
        return Unit.Value;
    }
}
