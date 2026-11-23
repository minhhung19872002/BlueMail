using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Auth;
using BlueMail.Dns;

namespace BlueMail.UnitTests.Auth;

/// <summary>
/// The openspf.org RFC 7208 test suite (release 2014.04), loaded verbatim: Data/rfc7208-tests.yml is the original
/// file; rfc7208-tests.json is its mechanical conversion (scripts/spf-suite-to-json.py) and must carry the YAML's hash.
/// </summary>
public class SpfSuiteTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Auth", "Data");

    private static JsonElement Suite { get; } = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "rfc7208-tests.json"))).RootElement;

    [Fact]
    public void Json_is_generated_from_the_committed_yaml()
    {
        var yamlHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(DataDir, "rfc7208-tests.yml"))));
        Suite.GetProperty("source_sha256").GetString().Should().Be(yamlHash);
    }

    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var scenario in Suite.GetProperty("scenarios").EnumerateArray())
        {
            foreach (var t in scenario.GetProperty("tests").EnumerateObject())
            {
                data.Add(i, t.Name);
            }

            i++;
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Rfc7208_suite(int scenarioIndex, string testName)
    {
        var scenario = Suite.GetProperty("scenarios")[scenarioIndex];
        var test = scenario.GetProperty("tests").GetProperty(testName);
        var dns = new ZoneDns(scenario.TryGetProperty("zonedata", out var z) ? z : default);
        // "DEFAULT" is the suite's stand-in for the receiver's default explanation.
        var spf = new SpfEvaluator(dns) { ReceiverHostname = "receiver.example", DefaultExplanation = "DEFAULT" };
        var outcome = await spf.CheckAsync(IPAddress.Parse(test.GetProperty("host").GetString()!), test.GetProperty("mailfrom").GetString()!,
            test.GetProperty("helo").GetString()!);

        var expected = test.GetProperty("result");
        var allowed = expected.ValueKind == JsonValueKind.Array
            ? expected.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [expected.GetString()!];
        allowed.Should().Contain(outcome.AuthResultsValue, $"{scenario.GetProperty("description").GetString()} / {testName}: {Describe(test)} (problem: {outcome.Problem})");
        if (test.TryGetProperty("explanation", out var exp) && outcome.Result == SpfResult.Fail)
        {
            // Case-insensitive: the suite writes ip6 nibbles in upper case; RFC 7208 7.4's own example uses lower case.
            outcome.Explanation.Should().BeEquivalentTo(exp.GetString(), testName);
        }
    }

    private static string Describe(JsonElement t) => t.TryGetProperty("description", out var d) ? d.GetString()! : "";

    /// <summary>Zone table of one scenario, with the suite's driver rule: SPF records also appear as TXT when a name has no TXT.</summary>
    private sealed class ZoneDns : ISpfDns
    {
        private readonly Dictionary<string, List<(string Type, JsonElement Value)>> _zone = new(StringComparer.OrdinalIgnoreCase);

        public ZoneDns(JsonElement zone)
        {
            if (zone.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var name in zone.EnumerateObject())
            {
                var list = new List<(string, JsonElement)>();
                foreach (var rec in name.Value.EnumerateArray())
                {
                    if (rec.ValueKind == JsonValueKind.String)
                    {
                        list.Add((rec.GetString()!, default)); // "TIMEOUT"
                    }
                    else
                    {
                        var p = rec.EnumerateObject().First();
                        list.Add((p.Name, p.Value));
                    }
                }

                // Driver rules of the suite: SPF records also answer TXT queries when the name has no TXT; the marker
                // TXT "NONE" means "no TXT records, do not copy" (so TIMEOUT then applies to the TXT query).
                var noTxtMarker = list.RemoveAll(r => r.Item1 == "TXT" && r.Item2.ValueKind == JsonValueKind.String && r.Item2.GetString() == "NONE") > 0;
                if (!noTxtMarker && !list.Any(r => r.Item1 == "TXT"))
                {
                    list.AddRange(list.Where(r => r.Item1 == "SPF").Select(r => ("TXT", r.Item2)).ToList());
                }

                _zone[name.Name.TrimEnd('.')] = list;
            }
        }

        private (DnsStatus, List<JsonElement>) Lookup(string name, string type, int depth = 0)
        {
            if (!_zone.TryGetValue(name.TrimEnd('.'), out var recs))
            {
                return (DnsStatus.NxDomain, []);
            }

            // TIMEOUT stands for the (obsolete) type-SPF query timing out: records of the asked type still answer.
            if (recs.Any(r => r.Type == "TIMEOUT") && !recs.Any(r => r.Type == type))
            {
                return (DnsStatus.TempFail, []);
            }

            var cname = recs.FirstOrDefault(r => r.Type == "CNAME");
            if (cname.Type is not null && type != "CNAME" && depth < 8)
            {
                return Lookup(cname.Value.GetString()!, type, depth + 1);
            }

            var matches = recs.Where(r => r.Type == type).Select(r => r.Value).ToList();
            return (matches.Count == 0 ? DnsStatus.NoData : DnsStatus.Ok, matches);
        }

        public Task<(DnsStatus Status, IReadOnlyList<string> Records)> TxtAsync(string name, CancellationToken ct)
        {
            var (s, recs) = Lookup(name, "TXT");
            IReadOnlyList<string> texts = recs.Select(v => v.ValueKind == JsonValueKind.Array ? string.Concat(v.EnumerateArray().Select(x => x.GetString())) : v.GetString()!).ToList();
            return Task.FromResult((s, texts));
        }

        public Task<(DnsStatus Status, IReadOnlyList<IPAddress> Addresses)> AddressesAsync(string name, AddressFamily family, CancellationToken ct)
        {
            var (s, recs) = Lookup(name, family == AddressFamily.InterNetwork ? "A" : "AAAA");
            IReadOnlyList<IPAddress> addrs = recs.Select(v => IPAddress.Parse(v.GetString()!)).ToList();
            return Task.FromResult((s, addrs));
        }

        public Task<(DnsStatus Status, IReadOnlyList<string> Hosts)> MxAsync(string name, CancellationToken ct)
        {
            var (s, recs) = Lookup(name, "MX");
            IReadOnlyList<string> hosts = recs.OrderBy(v => v[0].GetInt32()).Select(v => v[1].GetString()!).ToList();
            return Task.FromResult((s, hosts));
        }

        public Task<(DnsStatus Status, IReadOnlyList<string> Names)> PtrAsync(IPAddress address, CancellationToken ct)
        {
            var (s, recs) = Lookup(DnsClient.ReverseName(address), "PTR");
            IReadOnlyList<string> names = recs.Select(v => v.GetString()!).ToList();
            return Task.FromResult((s, names));
        }
    }
}
