using System.Globalization;
using System.Net;
using BlueMail.Dns;

namespace BlueMail.InteropTests;

/// <summary>
/// DnssecValidator and DANE policy against the dev doitac.test zone, signed by CoreDNS's dnssec plugin (an independent
/// signer), trusted through a private anchor like BM_DNSSEC_ANCHORS of the dev outbound service.
/// </summary>
public sealed class DnssecDevZoneTests
{
    private const string Anchor = "doitac.test 61090 13 2 2C2F21A49C4FB05B0CE60C0D6688D2043E095B5C5EF3DAB36CD4A60A98CA5EAE";

    private static readonly DnsClient Client = new(new DnsClientOptions
    {
        Servers = [new IPEndPoint(IPAddress.Loopback, int.Parse(Environment.GetEnvironmentVariable("BM_DEV_DNS_PORT") ?? "55353", CultureInfo.InvariantCulture))],
    });

    private static DnssecValidator Validator(string anchor = Anchor) => new(Client) { ExtraAnchors = DnssecAnchors.Parse(anchor) };

    [Theory]
    [InlineData("mail.doitac.test", DnsType.A, DnssecStatus.Secure, 1)]
    [InlineData("doitac.test", DnsType.MX, DnssecStatus.Secure, 1)]
    [InlineData("_25._tcp.mail.doitac.test", DnsType.TLSA, DnssecStatus.Secure, 1)]
    [InlineData("doitac.test", DnsType.TXT, DnssecStatus.Secure, 1)]
    [InlineData("no-such-name.doitac.test", DnsType.A, DnssecStatus.Secure, 0)]
    [InlineData("mail.doitac.test", DnsType.AAAA, DnssecStatus.Secure, 0)]
    [InlineData("bluemail.test", DnsType.MX, DnssecStatus.Indeterminate, 1)]
    public async Task Signed_zone_validates(string name, DnsType type, DnssecStatus status, int records)
    {
        var r = await Validator().ResolveAsync(name, type);
        r.Status.Should().Be(status, r.Reason);
        r.Records.Should().HaveCount(records);
    }

    [Fact]
    public async Task A_wrong_anchor_makes_the_zone_bogus()
    {
        var r = await Validator("doitac.test 61090 13 2 " + new string('A', 64)).ResolveAsync("mail.doitac.test", DnsType.A);
        r.Status.Should().Be(DnssecStatus.Bogus);
    }

    [Fact]
    public async Task Dane_policy_of_the_dev_mx_hosts()
    {
        var good = await Dane.PolicyAsync(Validator(), "mail.doitac.test", 25, mxSecure: true, default);
        good.State.Should().Be(DaneState.Required, good.Reason);
        good.Records.Should().ContainSingle(t => t.Usage == 3 && t.Selector == 1 && t.MatchingType == 1);

        var unsignedMx = await Dane.PolicyAsync(Validator(), "mail.doitac.test", 25, mxSecure: false, default);
        unsignedMx.State.Should().Be(DaneState.None);

        var outside = await Dane.PolicyAsync(Validator(), "mx.bluemail.test", 25, mxSecure: true, default);
        outside.State.Should().Be(DaneState.None); // no DNSSEC for bluemail.test: no DANE, not a failure
    }
}
