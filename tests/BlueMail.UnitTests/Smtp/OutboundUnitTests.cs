using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Mime;
using BlueMail.Outbound;
using BlueMail.Protocols.Smtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.UnitTests.Smtp;

public class RoutingTests
{
    [Fact]
    public void Mx_selection_follows_rfc5321_and_rfc7505()
    {
        var r = MxResolver.Select("x.test", DnsStatus.Ok, [new MxData(20, "b.x.test."), new MxData(10, "a.x.test."), new MxData(30, "c.x.test")]);
        r.Hosts.Select(h => h.Host).Should().Equal("a.x.test", "b.x.test", "c.x.test");
        MxResolver.Select("x.test", DnsStatus.NoData, []).Hosts.Single().Host.Should().Be("x.test", "implicit MX");
        MxResolver.Select("x.test", DnsStatus.Ok, [new MxData(0, ".")]).Error.Should().Be(RouteError.NullMx);
        MxResolver.Select("x.test", DnsStatus.NxDomain, []).Error.Should().Be(RouteError.NoSuchDomain);
        MxResolver.Select("x.test", DnsStatus.TempFail, []).Error.Should().Be(RouteError.Temporary, "never bounce on a DNS hiccup");
    }

    [Fact]
    public void Mta_sts_policy_parse_and_match()
    {
        var p = StsPolicy.Parse("version: STSv1\r\nmode: enforce\r\nmx: mail.example.com\r\nmx: *.example.net\r\nmax_age: 604800\r\n", "abc")!;
        p.Mode.Should().Be(StsMode.Enforce);
        p.Matches("mail.example.com").Should().BeTrue();
        p.Matches("mx1.example.net").Should().BeTrue();
        p.Matches("a.b.example.net").Should().BeFalse("wildcard covers one label only (RFC 8461 4.1)");
        p.Matches("example.net").Should().BeFalse();
        p.Matches("evil.com").Should().BeFalse();
        StsPolicy.Parse("version: STSv1\nmode: enforce\nmax_age: 10\n", "x").Should().BeNull("enforce without mx");
        StsPolicy.Parse("version: STSv2\nmode: none\nmax_age: 10\n", "x").Should().BeNull();
        StsPolicy.Parse("version: STSv1\nmode: sometimes\nmx: a\nmax_age: 10\n", "x").Should().BeNull();
        MtaStsResolver.ParseTxtId("v=STSv1; id=20261123T01;").Should().Be("20261123T01");
        MtaStsResolver.ParseTxtId("v=STSv1; id=bad-id!").Should().BeNull();
        MtaStsResolver.ParseTxtId("v=spf1 -all").Should().BeNull();
    }

    [Fact]
    public void Retry_schedule_and_reply_classification()
    {
        RetrySchedule.After(1).Should().Be(TimeSpan.FromMinutes(5));
        RetrySchedule.After(3).Should().Be(TimeSpan.FromMinutes(30));
        RetrySchedule.After(50).Should().Be(TimeSpan.FromHours(4));
        Throttle.IsRateLimit(421, "4.7.0", "Try again later").Should().BeTrue();
        Throttle.IsRateLimit(450, "4.7.28", "rate limited, slow down").Should().BeTrue();
        Throttle.IsRateLimit(451, "4.2.1", "Mailbox busy").Should().BeFalse();
        Throttle.IsUnknownUser(550, "5.1.1").Should().BeTrue();
        Throttle.IsUnknownUser(550, "5.7.1").Should().BeFalse("policy rejection is not a dead address");
    }

    [Theory]
    [InlineData("Báo giá", "Re: Báo giá")]
    [InlineData("Re: Báo giá", "Re: Báo giá")]
    [InlineData("TL: Báo giá", "TL: Báo giá")]
    [InlineData("AW: Angebot", "AW: Angebot")]
    [InlineData("Return policy", "Re: Return policy")]
    public void Reply_prefix_is_added_once(string subject, string expected) =>
        ComposeFromMessageHandler.Prefix("Re:", subject, ["re", "aw", "tl", "trả lời"]).Should().Be(expected);
}

public class DsnTests
{
    [Fact]
    public void Dsn_is_a_valid_multipart_report_in_english_and_vietnamese()
    {
        var raw = DsnBuilder.Build("out1.bluemail.test", "lan@bluemail.test", "vi", "Báo giá", "abc@bluemail.test",
            "From: lan@bluemail.test\r\nSubject: =?utf-8?B?QsOhbyBnacOh?=", [new DsnRecipient("x@partner.test", DsnAction.Failed, "5.1.1", "mx.partner.test",
                "550 5.1.1 No such user", DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow.AddMinutes(-1), "dsn-1@out1.bluemail.test", DateTimeOffset.UtcNow);
        var m = MimeMessage.Parse(raw);
        m.Root.MediaType.Should().Be("multipart/report");
        m.Root.ContentType["report-type"].Should().Be("delivery-status");
        m.InReplyTo.Should().Be("abc@bluemail.test");
        var stream = new MemoryStream(raw);
        var human = m.Root.Children[0].ReadText(stream);
        human.Should().Contain("không gửi được").And.Contain("could not be delivered").And.Contain("x@partner.test");
        var status = DeliveryStatus.Parse(m.Root.Children[1].ReadText(stream));
        status.Single().Action.Should().Be("failed");
        status.Single().Status.Should().Be("5.1.1");
        m.Root.Children[2].MediaType.Should().Be("text/rfc822-headers");
    }
}

public class SmtpClientTests
{
    private sealed class Capture : ISmtpHandler
    {
        public List<byte[]> Messages { get; } = [];
        public ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope s, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);
        public ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope s, SmtpPath f, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

        public ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope s, SmtpPath r, CancellationToken ct) =>
            ValueTask.FromResult(r.Address.StartsWith("no", StringComparison.Ordinal) ? new SmtpReply(550, "5.1.1", "nope") : (SmtpReply?)null);

        public ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope s, Stream m, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            m.CopyTo(ms);
            Messages.Add(ms.ToArray());
            return ValueTask.FromResult(new SmtpReply(250, "2.0.0", "ok"));
        }
    }

    private static X509Certificate2 Cert()
    {
        using var rsa = RSA.Create(2048);
        using var c = new CertificateRequest("CN=mx.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    private static async Task<(int Port, Task Server, Capture Handler)> Serve(X509Certificate2? cert, int connections = 1)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var h = new Capture();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < connections; i++)
            {
                using var c = await listener.AcceptTcpClientAsync();
                var session = new SmtpServerSession(new SmtpServerOptions { Hostname = "mx.test", StartTlsAvailable = cert is not null, MaxMessageBytes = 1_000_000 }, h,
                    new SmtpEnvelope { RemoteAddress = IPAddress.Loopback });
                await new SmtpTransport(c.GetStream(), session, new SmtpTransportOptions { Certificate = () => cert }, NullLogger.Instance).RunAsync(default);
            }

            listener.Stop();
        });
        await Task.Yield();
        return (((IPEndPoint)listener.LocalEndpoint).Port, server, h);
    }

    [Fact]
    public async Task Our_client_and_server_agree_byte_for_byte_with_tls_bdat_and_per_recipient_results()
    {
        using var cert = Cert();
        var (port, server, h) = await Serve(cert);
        var message = Encoding.UTF8.GetBytes("Subject: t\r\n\r\n.leading dot\r\nTiếng Việt\r\n..two dots\r\n");
        await using (var client = new SmtpClient(new SmtpClientOptions { Helo = "out.test" }))
        {
            await client.ConnectAsync("mx.test", IPAddress.Loopback, port, default);
            client.TlsActive.Should().BeTrue("STARTTLS is used whenever offered");
            client.Extensions.Should().Contain("CHUNKING").And.NotContain("STARTTLS");
            var (rcpts, final) = await client.SendAsync("a@out.test", ["yes@mx.test", "no@mx.test"], message, smtpUtf8: false, eightBit: true, default);
            rcpts.Single(r => r.Recipient == "no@mx.test").Response.Code.Should().Be(550);
            final!.Code.Should().Be(250);
            await client.QuitAsync(default);
        }

        await server;
        h.Messages.Single().Should().Equal(message);
    }

    [Fact]
    public async Task Dot_stuffing_path_without_chunking_round_trips()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource<string>();
        var server = Task.Run(async () =>
        {
            // Minimal server without CHUNKING, so the client must use DATA with dot-stuffing.
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            async Task Say(string l) => await s.WriteAsync(Encoding.UTF8.GetBytes(l + "\r\n"));
            await Say("220 plain.test ESMTP");
            await r.ReadLineAsync();
            await Say("250-plain.test");
            await Say("250 8BITMIME");
            await r.ReadLineAsync();
            await Say("250 ok");
            await r.ReadLineAsync();
            await Say("250 ok");
            await r.ReadLineAsync();
            await Say("354 go");
            var sb = new StringBuilder();
            string? line;
            while ((line = await r.ReadLineAsync()) != ".")
            {
                sb.Append(line).Append('\n');
            }

            received.SetResult(sb.ToString());
            await Say("250 done");
            await r.ReadLineAsync();
            await Say("221 bye");
            listener.Stop();
        });

        await using var client = new SmtpClient(new SmtpClientOptions { Helo = "out.test" });
        await client.ConnectAsync("plain.test", IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, default);
        client.TlsActive.Should().BeFalse("no STARTTLS offered: opportunistic TLS continues in plaintext");
        var (_, final) = await client.SendAsync("a@x.test", ["b@plain.test"], Encoding.UTF8.GetBytes("Subject: s\r\n\r\n.x\r\nend"), false, true, default);
        final!.Code.Should().Be(250);
        await client.QuitAsync(default);
        await server;
        (await received.Task).Should().Be("Subject: s\n\n..x\nend\n", "leading dots doubled; a final CRLF added");
    }

    [Fact]
    public async Task Required_tls_fails_closed_against_an_untrusted_certificate()
    {
        using var cert = Cert(); // self-signed: not trusted
        var (port, server, _) = await Serve(cert);
        await using var client = new SmtpClient(new SmtpClientOptions { Helo = "out.test", Tls = TlsRequirement.Required, ValidCertificateNames = ["mx.test"] });
        var act = () => client.ConnectAsync("mx.test", IPAddress.Loopback, port, default);
        await act.Should().ThrowAsync<SmtpClientException>("MTA-STS enforce never falls back to an unauthenticated or plaintext session");
        await Task.WhenAny(server, Task.Delay(3000));
    }
}
