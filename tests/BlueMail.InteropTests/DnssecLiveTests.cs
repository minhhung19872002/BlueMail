using System.Net;
using System.Text.Json.Nodes;
using BlueMail.Dns;

namespace BlueMail.InteropTests;

/// <summary>
/// DnssecValidator against the real DNS (public resolvers, CD set so their own validation does not hide bogus data):
/// signed names validate from the root key, unsigned zones are insecure, the deliberately broken test zone is bogus.
/// Needs Internet access; set BM_OFFLINE=1 to skip.
/// </summary>
public sealed class DnssecLiveTests
{
    private static readonly DnsClient Client = new(new DnsClientOptions
    {
        Servers = [new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53)],
        Timeout = TimeSpan.FromSeconds(4),
    });

    private static bool Offline => Environment.GetEnvironmentVariable("BM_OFFLINE") == "1";

    [Theory]
    [InlineData("ietf.org", DnsType.A, DnssecStatus.Secure)]
    [InlineData("ietf.org", DnsType.MX, DnssecStatus.Secure)]
    [InlineData("www.isc.org", DnsType.A, DnssecStatus.Secure)]
    [InlineData("cloudflare.com", DnsType.A, DnssecStatus.Secure)]
    [InlineData("bluemail-no-such-name-4711.ietf.org", DnsType.A, DnssecStatus.Secure)]
    [InlineData("ietf.org", DnsType.TLSA, DnssecStatus.Secure)]
    [InlineData("google.com", DnsType.A, DnssecStatus.Insecure)]
    [InlineData("dnssec-failed.org", DnsType.A, DnssecStatus.Bogus)]
    public async Task Validates_real_zones(string name, DnsType type, DnssecStatus expected)
    {
        if (Offline)
        {
            return;
        }

        var v = new DnssecValidator(Client);
        var r = await v.ResolveAsync(name, type);
        r.Status.Should().Be(expected, r.Reason);
    }

    private static readonly string[] Names =
    [
        "ietf.org:A", "ietf.org:MX", "ietf.org:TLSA", "www.isc.org:A", "cloudflare.com:A", "no-such-name-4711.cloudflare.com:A", "nic.cz:A",
        "nlnetlabs.nl:MX", "sidn.nl:A", "example.com:A", "www.example.com:AAAA", "bluemail-no-such-name-4711.ietf.org:A", "google.com:A",
        "microsoft.com:MX", "dnssec-failed.org:A", "isc.org:DS",
    ];

    /// <summary>Phase 12 acceptance: the same verdicts as an independent validator built on dnspython (tests/interop/dnssec_check.py).</summary>
    [Fact]
    public async Task Agrees_with_dnspython_on_real_zones()
    {
        if (Offline)
        {
            return;
        }

        var (code, stdout, stderr) = ToolsContainer.Exec(["python", "/work/tests/interop/dnssec_check.py", "1.1.1.1", .. Names]);
        code.Should().Be(0, stderr);
        var reference = JsonNode.Parse(stdout)!.AsObject();
        var v = new DnssecValidator(Client);
        var mismatches = new List<string>();
        foreach (var item in Names)
        {
            var sep = item.LastIndexOf(':');
            var ours = await v.ResolveAsync(item[..sep], Enum.Parse<DnsType>(item[(sep + 1)..]));
            if (ours.Status.ToString() != reference[item]!.GetValue<string>())
            {
                mismatches.Add($"{item}: BlueMail {ours.Status} ({ours.Reason}), dnspython {reference[item]}");
            }
        }

        mismatches.Should().BeEmpty();
    }
}
