using System.Collections.Concurrent;
using BlueMail.Application.Features.Admin.Domains;
using BlueMail.Dns;

namespace BlueMail.IntegrationTests.Infrastructure;

public sealed class FakeDomainDns : IDomainDns
{
    public ConcurrentDictionary<(string Type, string Name), string[]> Records { get; } = new();

    private Task<(DnsStatus Status, IReadOnlyList<string> Values)> Get(string type, string name) =>
        Task.FromResult(Records.TryGetValue((type, name.ToLowerInvariant()), out var v) ? (DnsStatus.Ok, (IReadOnlyList<string>)v) : (DnsStatus.NxDomain, (IReadOnlyList<string>)[]));

    public Task<(DnsStatus Status, IReadOnlyList<string> Values)> TxtAsync(string name, CancellationToken ct) => Get("TXT", name);
    public Task<(DnsStatus Status, IReadOnlyList<string> Values)> MxAsync(string name, CancellationToken ct) => Get("MX", name);
    public Task<(DnsStatus Status, IReadOnlyList<string> Values)> CnameAsync(string name, CancellationToken ct) => Get("CNAME", name);
    public Task<(DnsStatus Status, IReadOnlyList<string> Values)> SrvAsync(string name, CancellationToken ct) => Get("SRV", name);
}
