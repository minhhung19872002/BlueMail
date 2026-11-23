using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;

namespace BlueMail.UnitTests.Smtp;

/// <summary>Phase 12 sending pieces that need no network: VERP, report parsing, warm-up, DANE matching, DNSSEC helpers.</summary>
public sealed class DeliverabilityTests
{
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Verp_round_trips_and_refuses_forgeries()
    {
        var verp = new Verp(MasterKey);
        var id = Guid.NewGuid();
        var address = verp.Encode(id, "Acme.Test");
        address.Should().StartWith("bounces+").And.EndWith("@acme.test");
        verp.Decode(address).Should().Be(id);
        verp.Decode(address.ToUpperInvariant()).Should().Be(id, "local parts may come back in another case");

        var at = address.IndexOf('@', StringComparison.Ordinal);
        var tampered = address[..(at - 1)] + (address[at - 1] == 'a' ? 'b' : 'a') + address[at..];
        verp.Decode(tampered).Should().BeNull();
        verp.Decode($"bounces+{Guid.NewGuid():N}.000000000000@acme.test").Should().BeNull();
        new Verp(new byte[32]).Decode(address).Should().BeNull("another installation's key");
        verp.Decode("someone@acme.test").Should().BeNull();
    }

    private static byte[] Crlf(string s) => Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

    [Fact]
    public void Parses_a_late_dsn()
    {
        var raw = Crlf("""
            From: MAILER-DAEMON@mx.example.org
            To: bounces+x@acme.test
            Subject: Undelivered Mail Returned to Sender
            MIME-Version: 1.0
            Content-Type: multipart/report; report-type=delivery-status; boundary="b1"

            --b1
            Content-Type: text/plain

            The mailbox does not exist.
            --b1
            Content-Type: message/delivery-status

            Reporting-MTA: dns; mx.example.org
            Arrival-Date: Mon, 23 Nov 2026 10:00:00 +0000

            Final-Recipient: rfc822; Gone@Example.org
            Action: failed
            Status: 5.1.1
            Diagnostic-Code: smtp; 550 5.1.1 <gone@example.org>: user
              unknown

            Final-Recipient: rfc822; later@example.org
            Action: delayed
            Status: 4.4.1
            --b1
            Content-Type: text/rfc822-headers

            From: lan@acme.test
            Message-ID: <abc123@acme.test>
            Subject: hello
            --b1--
            """);
        var r = ReportParser.Parse(raw);
        r.Kind.Should().Be(ReportKind.DeliveryStatus);
        r.ReportingMta.Should().Be("mx.example.org");
        r.OriginalMessageId.Should().Be("abc123@acme.test");
        r.Recipients.Should().HaveCount(2);
        r.Recipients[0].Should().Be(new ReportRecipient("Gone@Example.org", "failed", "5.1.1", "smtp; 550 5.1.1 <gone@example.org>: user unknown"));
        r.Recipients[1].Action.Should().Be("delayed");
    }

    [Fact]
    public void Parses_an_arf_complaint()
    {
        var raw = Crlf("""
            From: fbl@isp.example
            To: fbl@bluemail.test
            Subject: complaint
            MIME-Version: 1.0
            Content-Type: multipart/report; report-type=feedback-report; boundary="b2"

            --b2
            Content-Type: text/plain

            This is an email abuse report.
            --b2
            Content-Type: message/feedback-report

            Feedback-Type: abuse
            User-Agent: SomeISP/1.0
            Version: 1
            Original-Mail-From: <bounces+0011@acme.test>
            Original-Rcpt-To: <victim@isp.example>
            Reporting-MTA: dns; mail.isp.example
            --b2
            Content-Type: message/rfc822

            From: lan@acme.test
            To: victim@isp.example
            Message-ID: <promo-1@acme.test>
            Subject: Buy now

            body
            --b2--
            """);
        var r = ReportParser.Parse(raw);
        r.Kind.Should().Be(ReportKind.Feedback);
        r.FeedbackType.Should().Be("abuse");
        r.OriginalRcpt.Should().Be("victim@isp.example");
        r.OriginalMailFrom.Should().Be("bounces+0011@acme.test");
        r.OriginalMessageId.Should().Be("promo-1@acme.test");
    }

    [Fact]
    public void Ordinary_mail_is_not_a_report() =>
        ReportParser.Parse(Crlf("From: a@b.test\nSubject: hi\n\nhello\n")).Kind.Should().Be(ReportKind.None);

    [Fact]
    public void Warm_up_rises_then_ends()
    {
        var start = new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);
        WarmupSchedule.CapOn(null, start).Should().BeNull();
        WarmupSchedule.CapOn(start, start).Should().Be(50);
        WarmupSchedule.CapOn(start, start.AddHours(3)).Should().Be(100, "a new UTC day started");
        var caps = Enumerable.Range(0, WarmupSchedule.Days).Select(d => WarmupSchedule.CapOn(start, start.AddDays(d))!.Value).ToList();
        caps.Should().BeInAscendingOrder();
        WarmupSchedule.CapOn(start, start.AddDays(WarmupSchedule.Days)).Should().BeNull();
    }

    [Fact]
    public void Dane_matches_by_key_or_certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=mx.example.org", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(-2)); // expired: DANE-EE ignores dates
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        Dane.Match(new TlsaData(3, 1, 1, SHA256.HashData(spki)), cert).Should().BeTrue();
        Dane.Match(new TlsaData(3, 1, 2, SHA512.HashData(spki)), cert).Should().BeTrue();
        Dane.Match(new TlsaData(3, 0, 0, cert.RawData), cert).Should().BeTrue();
        Dane.Match(new TlsaData(3, 1, 1, new byte[32]), cert).Should().BeFalse();

        var policy = new DanePolicy(DaneState.Required, [new TlsaData(3, 1, 1, SHA256.HashData(spki))]);
        Dane.Matches(policy, "other.example.org", cert, []).Should().BeTrue("DANE-EE does not check names");
        Dane.Matches(policy with { Records = [new TlsaData(3, 1, 1, new byte[32])] }, "mx.example.org", cert, []).Should().BeFalse();
        Dane.Usable(new TlsaData(1, 1, 1, [])).Should().BeFalse("PKIX-EE is not used for SMTP (RFC 7672 3.1.3)");
    }

    [Fact]
    public void Nsec3_hash_matches_rfc5155_appendix_a() =>
        Convert.ToHexString(DnssecValidator.Nsec3Hash("example", Convert.FromHexString("AABBCCDD"), 12))
            .Should().Be(Convert.ToHexString(DnssecValidator.Base32Hex("0p9mhaveqvm6t7vbl5lop2u3t2rp3tom")));

    [Fact]
    public void Canonical_order_follows_rfc4034_section_6_1()
    {
        string[] expected = ["example.", "a.example.", "yljkjljk.a.example.", "Z.a.example.", "zABC.a.EXAMPLE.", "z.example.", "*.z.example."];
        expected.Reverse().Order(Comparer<string>.Create(DnssecValidator.CanonicalCompare)).Take(2).Should().Equal("example.", "a.example.");
        for (var i = 1; i < expected.Length; i++)
        {
            DnssecValidator.CanonicalCompare(expected[i - 1], expected[i]).Should().BeNegative($"{expected[i - 1]} < {expected[i]}");
        }
    }

    [Fact]
    public void Anchors_parse()
    {
        var a = DnssecAnchors.Parse("Doitac.Test 61090 13 2 2C2F; example.org 1 8 2 AB");
        a.Should().ContainKey("doitac.test.").And.ContainKey("example.org.");
        a["doitac.test."].Single().Should().BeEquivalentTo(new DsData(61090, 13, 2, [0x2C, 0x2F]));
        FluentActions.Invoking(() => DnssecAnchors.Parse("bad")).Should().Throw<FormatException>();
    }
}
