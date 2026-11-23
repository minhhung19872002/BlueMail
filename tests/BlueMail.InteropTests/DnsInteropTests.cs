using System.Net;
using System.Text.Json.Nodes;
using BlueMail.Dns;

namespace BlueMail.InteropTests;

/// <summary>BlueMail.Dns returns the same answers as dnspython on the dev CoreDNS (Phase 2 acceptance).</summary>
public sealed class DnsInteropTests
{
    /// <summary>CoreDNS as published on the host (deploy/docker-compose.dev.yml, BM_DEV_DNS_PORT).</summary>
    private static readonly IPEndPoint HostDns = new(IPAddress.Loopback, int.Parse(Environment.GetEnvironmentVariable("BM_DEV_DNS_PORT") ?? "55353", System.Globalization.CultureInfo.InvariantCulture));

    private static readonly string[] Queries =
    [
        "mx.bluemail.test:A", "bluemail.test:MX", "acme.test:MX", "doitac.test:MX", "doitac.test:TXT", "_dmarc.doitac.test:TXT",
        "bm2026._domainkey.bluemail.test:TXT", "dt2026._domainkey.doitac.test:TXT", "bigtxt.bluemail.test:TXT",
        "alias1.bluemail.test:A", "alias1.bluemail.test:CNAME", "nullmx.doitac.test:MX", "nomx.doitac.test:MX", "nomx.doitac.test:A",
        "mx.bluemail.test:AAAA", "nonexistent.bluemail.test:A", "bluemail.test:SOA", "bluemail.test:NS",
        "_imaps._tcp.bluemail.test:SRV", "26.0.77.10.in-addr.arpa:PTR", "30.0.77.10.in-addr.arpa:PTR",
    ];

    [Fact]
    public async Task Same_answers_as_dnspython()
    {
        var (code, stdout, stderr) = ToolsContainer.Exec(["python", "/work/tests/interop/dns_dump.py", "10.77.0.53", .. Queries]);
        code.Should().Be(0, stderr);
        var reference = JsonNode.Parse(stdout)!.AsObject();
        var client = new DnsClient(new DnsClientOptions { Servers = [HostDns] });
        var mismatches = new List<string>();

        foreach (var q in Queries)
        {
            var sep = q.LastIndexOf(':');
            var type = Enum.Parse<DnsType>(q[(sep + 1)..]);
            var r = await client.ResolveAsync(q[..sep], type);
            var ours = r.Records.Where(x => x.Type == type).Select(Text).Order(StringComparer.Ordinal).ToList();
            var expected = reference[q]!;
            var expectedRecords = expected["records"]!.AsArray().Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
            if (r.Status.ToString() != expected["status"]!.GetValue<string>() || !ours.SequenceEqual(expectedRecords))
            {
                mismatches.Add($"{q}: dnspython {expected["status"]} [{string.Join(" | ", expectedRecords)}] vs ours {r.Status} [{string.Join(" | ", ours)}]");
            }

            if (r.Status == DnsStatus.Ok && expected["canonical"] is { } canonical && !DnsClient.NameEq(canonical.GetValue<string>(), r.CanonicalName))
            {
                mismatches.Add($"{q}: canonical name {canonical} vs {r.CanonicalName}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Truncated_udp_answer_is_retried_over_tcp()
    {
        var client = new DnsClient(new DnsClientOptions { Servers = [HostDns], UdpPayloadSize = 512 });
        var r = await client.QueryAsync("bigtxt.bluemail.test.", DnsType.TXT);
        r.Status.Should().Be(DnsStatus.Ok);
        r.Records.Should().HaveCount(40, "all 40 strings arrive only through TCP");
    }

    [Fact]
    public async Task Null_mx_and_negative_answers_are_cached_as_such()
    {
        var client = new DnsClient(new DnsClientOptions { Servers = [HostDns] });
        var (status, mx) = await client.LookupMxAsync("nullmx.doitac.test");
        status.Should().Be(DnsStatus.Ok);
        mx.Should().ContainSingle(m => m.Preference == 0 && m.Exchange == ".");

        (await client.ResolveAsync("nonexistent.bluemail.test", DnsType.A)).Status.Should().Be(DnsStatus.NxDomain);
        var again = await client.ResolveAsync("nonexistent.bluemail.test", DnsType.A);
        again.Status.Should().Be(DnsStatus.NxDomain);
        again.FromCache.Should().BeTrue();
        (await client.ResolveAsync("mx.bluemail.test", DnsType.AAAA)).Status.Should().Be(DnsStatus.NoData);
    }

    [Fact]
    public async Task Unreachable_server_is_a_temporary_failure_not_an_exception()
    {
        var client = new DnsClient(new DnsClientOptions { Servers = [new IPEndPoint(IPAddress.Loopback, 9)], Timeout = TimeSpan.FromMilliseconds(300), Attempts = 1 });
        (await client.ResolveAsync("bluemail.test", DnsType.MX)).Status.Should().Be(DnsStatus.TempFail);
    }

    private static string Text(DnsRecord r) => r.Data switch
    {
        MxData m => $"{m.Preference} {m.Exchange.ToLowerInvariant()}",
        SrvData s => $"{s.Priority} {s.Weight} {s.Port} {s.Target.ToLowerInvariant()}",
        SoaData s => $"{s.MName.ToLowerInvariant()} {s.RName.ToLowerInvariant()} {s.Serial} {s.Refresh} {s.Retry} {s.Expire} {s.Minimum}",
        TxtData t => t.Text,
        string n => n.ToLowerInvariant(),
        _ => r.Data.ToString()!.ToLowerInvariant(),
    };
}
