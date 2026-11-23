using System.Net;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Outbound;

/// <summary>The address a delivery leaves from and the HELO name that goes with it.</summary>
public sealed record SourceIp(int? Id, IPAddress? Address, string Helo);

/// <summary>
/// Picks the sending IP for a tenant (PROMPT 2.4): the tenant's pool (or the default pool), only addresses whose PTR
/// names their HELO and resolves back, below the warm-up cap of the day, least used first. Without any configured
/// pool (or none for the tenant and no default pool) the host's own address and the configured HELO are used. Pools are reloaded every minute, PTRs every hour.
/// </summary>
public sealed class SendingIps(IServiceScopeFactory scopes, DnsClient dns, OutboundOptions options, ILogger<SendingIps> log) : IDisposable
{
    private readonly SemaphoreSlim _reload = new(1, 1);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private List<IpPool> _pools = [];
    private List<SendingIp> _ips = [];
    private Dictionary<Guid, Guid?> _tenantPools = [];

    public void Dispose() => _reload.Dispose();

    /// <summary>Null when the pool exists but every address is at its cap or unusable now (try later).</summary>
    public async Task<SourceIp?> ChooseAsync(Guid tenant, CancellationToken ct)
    {
        await LoadAsync(ct);
        if (_ips.Count == 0)
        {
            return new SourceIp(null, null, options.Helo);
        }

        var poolId = _tenantPools.GetValueOrDefault(tenant) ?? _pools.FirstOrDefault(p => p.IsDefault)?.Id;
        if (poolId is null)
        {
            return new SourceIp(null, null, options.Helo); // no pool applies to this tenant: the host's own address
        }

        var candidates = _ips.Where(i => i.PoolId == poolId && i.Enabled && i.PtrOk != false).ToList();
        if (candidates.Count == 0)
        {
            log.LogWarning("No usable sending IP in pool {Pool} for tenant {Tenant}", poolId, tenant);
            return null;
        }

        var now = options.Clock();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var ids = candidates.Select(c => c.Id).ToList();
        var used = await db.SendingIpDays.AsNoTracking().Where(d => d.Day == today && ids.Contains(d.SendingIpId)).ToDictionaryAsync(d => d.SendingIpId, d => d.Sent, ct);
        var pick = candidates
            .Select(c => (Ip: c, Used: used.GetValueOrDefault(c.Id), Cap: WarmupSchedule.CapOn(c.WarmupStartedAt, now)))
            .Where(x => x.Cap is null || x.Used < x.Cap)
            .OrderBy(x => x.Cap is null ? 0 : 1) // warm addresses first, new ones take what is left of their cap
            .ThenBy(x => x.Used)
            .FirstOrDefault();
        return pick.Ip is null ? null : new SourceIp(pick.Ip.Id, IPAddress.Parse(pick.Ip.Address), pick.Ip.Helo);
    }

    /// <summary>Counts messages handed over from an address today (warm-up cap and reporting).</summary>
    public async Task CountAsync(int ipId, int messages, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var today = DateOnly.FromDateTime(options.Clock().UtcDateTime);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.sending_ip_days (sending_ip_id, day, sent) VALUES ({ipId}, {today}, {messages})
            ON CONFLICT (sending_ip_id, day) DO UPDATE SET sent = mta.sending_ip_days.sent + excluded.sent
            """, ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (options.Clock() - _loadedAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        await _reload.WaitAsync(ct);
        try
        {
            if (options.Clock() - _loadedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            _pools = await db.IpPools.AsNoTracking().ToListAsync(ct);
            var ips = await db.SendingIps.ToListAsync(ct);
            foreach (var ip in ips.Where(i => i.Enabled && (i.PtrCheckedAt is null || options.Clock() - i.PtrCheckedAt > TimeSpan.FromHours(1))))
            {
                ip.PtrOk = await PtrMatchesAsync(ip, ct);
                ip.PtrCheckedAt = options.Clock();
                if (ip.PtrOk == false)
                {
                    log.LogWarning("Sending IP {Ip}: PTR does not name {Helo}; not used", ip.Address, ip.Helo);
                }
            }

            await db.SaveChangesAsync(ct);
            _ips = ips;
            _tenantPools = await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.IpPoolId != null).ToDictionaryAsync(t => t.Id, t => t.IpPoolId, ct);
            _loadedAt = options.Clock();
        }
        finally
        {
            _reload.Release();
        }
    }

    /// <summary>Forward-confirmed reverse DNS: PTR of the address is the HELO name and that name resolves to the address.</summary>
    private async Task<bool?> PtrMatchesAsync(SendingIp ip, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip.Address, out var address))
        {
            return false;
        }

        var (status, names) = await dns.LookupPtrAsync(address, ct);
        if (status == DnsStatus.TempFail)
        {
            return ip.PtrOk; // keep the last answer
        }

        if (!names.Any(n => n.TrimEnd('.').Equals(ip.Helo.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var (s2, addrs) = await dns.LookupAddressesAsync(ip.Helo, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6, ct);
        return s2 == DnsStatus.Ok && addrs.Contains(address);
    }
}
