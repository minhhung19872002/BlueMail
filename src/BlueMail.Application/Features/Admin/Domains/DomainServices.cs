using System.Net;
using BlueMail.Dns;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.Application.Features.Admin.Domains;

public static class DomainServiceRegistration
{
    /// <summary>DNS for the domain wizard: BM_DNS_SERVERS ("10.77.0.53,1.1.1.1" or "127.0.0.1:55353"), else the system's resolvers.</summary>
    public static IServiceCollection AddBlueMailDomainAdmin(this IServiceCollection services, IConfiguration cfg)
    {
        var servers = (cfg["BM_DNS_SERVERS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => IPEndPoint.TryParse(s, out var ep) ? (ep.Port == 0 ? new IPEndPoint(ep.Address, 53) : ep) : null)
            .Where(e => e is not null).Select(e => e!).ToList();
        if (servers.Count == 0)
        {
            servers = SystemResolvers.Read();
        }

        services.AddSingleton(new DnsClient(new DnsClientOptions { Servers = servers }));
        services.AddSingleton<IDomainDns, ResolverDomainDns>();
        services.AddSingleton<DomainRecordPlanner>();
        services.AddScoped<DomainService>();
        services.AddSingleton<DomainWatchWorker>();
        return services;
    }
}

/// <summary>
/// MTA-STS policy (RFC 8461 3.2) for every verified domain, served at https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt
/// (the domain's mta-sts host is a CNAME to ours). Mode "testing" until the administrator's records are all right,
/// then "enforce"; senders cache it for a day.
/// </summary>
public sealed class MtaStsPolicy(BlueMailDbContext db, BmSettings settings)
{
    public async Task<string?> ForHostAsync(string host, CancellationToken ct)
    {
        host = host.ToLowerInvariant();
        if (!host.StartsWith("mta-sts.", StringComparison.Ordinal))
        {
            return null;
        }

        var domain = host["mta-sts.".Length..];
        var d = await db.Domains.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Domain == domain && x.DeletedAt == null && x.VerifiedAt != null, ct);
        if (d is null)
        {
            return null;
        }

        var mode = d.MxOk && d.MtaStsOk ? "enforce" : "testing";
        return $"version: STSv1\r\nmode: {mode}\r\nmx: {settings.MailHostname}\r\nmax_age: 86400\r\n";
    }
}
