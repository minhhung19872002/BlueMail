using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlueMail.Protocols.Smtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.UnitTests.Smtp;

internal sealed class FakeHandler : ISmtpHandler
{
    public List<byte[]> Messages { get; } = [];
    public HashSet<string> Known { get; } = new(StringComparer.OrdinalIgnoreCase) { "lan@bluemail.test", "minh@bluemail.test" };

    public ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope s, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

    public ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope s, SmtpPath from, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

    public ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope s, SmtpPath r, CancellationToken ct) =>
        ValueTask.FromResult(Known.Contains(r.Address) ? null : (SmtpReply?)new SmtpReply(550, "5.1.1", "No such user"));

    public ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope s, Stream message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        message.CopyTo(ms);
        Messages.Add(ms.ToArray());
        return ValueTask.FromResult(new SmtpReply(250, "2.0.0", "Queued"));
    }
}

public class SmtpSessionTests
{
    private static SmtpServerSession New(FakeHandler h, bool tls = false) =>
        new(new SmtpServerOptions { Hostname = "mx.bluemail.test", MaxRecipients = 2, MaxMessageBytes = 1000, StartTlsAvailable = tls }, h,
            new SmtpEnvelope { RemoteAddress = IPAddress.Loopback });

    private static async Task<int> Code(SmtpServerSession s, string line) => (await s.HandleLineAsync(line, false, default)).Replies[^1].Code;

    [Fact]
    public async Task Commands_out_of_order_are_503()
    {
        var s = New(new FakeHandler());
        (await Code(s, "MAIL FROM:<a@x.test>")).Should().Be(503, "HELO first");
        (await Code(s, "EHLO client.test")).Should().Be(250);
        (await Code(s, "RCPT TO:<lan@bluemail.test>")).Should().Be(503, "MAIL first");
        (await Code(s, "DATA")).Should().Be(503);
        (await Code(s, "MAIL FROM:<a@x.test>")).Should().Be(250);
        (await Code(s, "MAIL FROM:<a@x.test>")).Should().Be(503, "nested MAIL");
        (await Code(s, "DATA")).Should().Be(554, "no valid recipients");
    }

    [Fact]
    public async Task Unknown_recipient_is_refused_at_rcpt_and_limits_apply()
    {
        var s = New(new FakeHandler());
        await Code(s, "EHLO c.test");
        await Code(s, "MAIL FROM:<>");
        (await Code(s, "RCPT TO:<nobody@bluemail.test>")).Should().Be(550);
        (await Code(s, "RCPT TO:<lan@bluemail.test>")).Should().Be(250);
        (await Code(s, "RCPT TO:<minh@bluemail.test>")).Should().Be(250);
        (await Code(s, "RCPT TO:<lan2@bluemail.test>")).Should().Be(452, "recipient limit (RFC 5321 4.5.3.1.10)");
    }

    [Theory]
    [InlineData("MAIL FROM:<a@x.test> SIZE=5000", 552)]
    [InlineData("MAIL FROM:<a@x.test> BODY=WEIRD", 555)]
    [InlineData("MAIL FROM:<a@x.test> FOO=1", 555)]
    [InlineData("MAIL FROM:<ân@x.test>", 553)]               // UTF-8 without SMTPUTF8
    [InlineData("MAIL FROM:<ân@x.test> SMTPUTF8", 250)]
    [InlineData("MAIL FROM:a@x.test", 250)]                  // missing brackets tolerated
    [InlineData("MAIL FROM:<@relay.test:a@x.test>", 250)]    // source route dropped
    [InlineData("MAIL FROM:<a@@x.test>", 501)]
    [InlineData("MAIL FROM:<a b@x.test>", 501)]
    public async Task Mail_from_parameters(string line, int expected)
    {
        var s = New(new FakeHandler());
        await Code(s, "EHLO c.test");
        (await Code(s, line)).Should().Be(expected);
    }

    [Fact]
    public async Task Ehlo_advertises_extensions_and_starttls_only_before_tls()
    {
        var s = New(new FakeHandler(), tls: true);
        var lines = (await s.HandleLineAsync("EHLO c.test", false, default)).Replies[0].Lines;
        lines.Should().Contain(["PIPELINING", "SIZE 1000", "8BITMIME", "SMTPUTF8", "ENHANCEDSTATUSCODES", "CHUNKING", "STARTTLS"]);
        (await s.HandleLineAsync("STARTTLS", false, default)).Action.Should().Be(SmtpAction.StartTls);
        s.TlsEstablished();
        (await Code(s, "MAIL FROM:<a@x.test>")).Should().Be(503, "TLS restarts the session: EHLO again");
        (await s.HandleLineAsync("EHLO c.test", false, default)).Replies[0].Lines.Should().NotContain("STARTTLS");
    }

    [Fact]
    public async Task Bdat_rules()
    {
        var h = new FakeHandler();
        var s = New(h);
        await Code(s, "EHLO c.test");
        await Code(s, "MAIL FROM:<a@x.test>");
        await Code(s, "RCPT TO:<lan@bluemail.test>");
        var step = await s.HandleLineAsync("BDAT 10", false, default);
        step.Action.Should().Be(SmtpAction.ReadChunk);
        step.ChunkSize.Should().Be(10);
        s.ChunkReceived(10, false).Replies[0].Code.Should().Be(250);
        (await Code(s, "DATA")).Should().Be(503, "only BDAT/RSET/QUIT/NOOP during BDAT");
        (await s.HandleLineAsync("BDAT 0 LAST", false, default)).LastChunk.Should().BeTrue();
        s.BdatReadyToDeliver.Should().BeTrue();
    }

    [Fact]
    public async Task Too_many_errors_close_the_connection()
    {
        var s = New(new FakeHandler());
        SmtpStep step = SmtpStep.Reply(SmtpReply.Ok);
        for (var i = 0; i < 10; i++)
        {
            step = await s.HandleLineAsync("BOGUS", false, default);
        }

        step.Action.Should().Be(SmtpAction.Close);
        step.Replies[^1].Code.Should().Be(421);
    }

    [Fact]
    public void Reply_formatting_multiline_and_enhanced()
    {
        Encoding.ASCII.GetString(new SmtpReply(250, "2.0.0", "a", "b").ToBytes(true)).Should().Be("250-2.0.0 a\r\n250 b\r\n");
        Encoding.ASCII.GetString(new SmtpReply(550, "5.1.1", "No").ToBytes(false)).Should().Be("550 No\r\n");
    }
}

public class SmtpTransportTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=mx.bluemail.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    private static async Task<(TcpClient Client, Task Server, FakeHandler Handler)> Start(long maxBytes = 1_000_000, X509Certificate2? cert = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var handler = new FakeHandler();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            listener.Stop();
            var session = new SmtpServerSession(new SmtpServerOptions { Hostname = "mx.bluemail.test", MaxMessageBytes = maxBytes, StartTlsAvailable = cert is not null },
                handler, new SmtpEnvelope { RemoteAddress = IPAddress.Loopback });
            await new SmtpTransport(socket.GetStream(), session, new SmtpTransportOptions { Certificate = () => cert }, NullLogger.Instance).RunAsync(default);
        });
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        return (client, server, handler);
    }

    private static async Task<string> Converse(Stream s, string send, int expectReplies)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(send));
        var sb = new StringBuilder();
        var buf = new byte[4096];
        var complete = 0;
        while (complete < expectReplies)
        {
            var n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (n == 0)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            complete = sb.ToString().Split("\r\n").Count(l => l.Length >= 4 && l[3] == ' ');
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Pipelined_transaction_with_dot_stuffing_is_delivered_exactly()
    {
        var (client, server, h) = await Start();
        var s = client.GetStream();
        (await Converse(s, "", 1)).Should().StartWith("220");
        var replies = await Converse(s, "EHLO c.test\r\nMAIL FROM:<a@x.test>\r\nRCPT TO:<lan@bluemail.test>\r\nDATA\r\n", 4);
        replies.Should().Contain("354");
        var done = await Converse(s, "Subject: t\r\n\r\n..leading dot\r\n.\r\n", 1);
        done.Should().Contain("250 2.0.0 Queued");
        await Converse(s, "QUIT\r\n", 1);
        await server;
        Encoding.ASCII.GetString(h.Messages.Single()).Should().Be("Subject: t\r\n\r\n.leading dot\r\n");
    }

    [Fact]
    public async Task Bdat_chunks_are_joined_without_dot_processing()
    {
        var (client, server, h) = await Start();
        var s = client.GetStream();
        await Converse(s, "", 1);
        await Converse(s, "EHLO c.test\r\nMAIL FROM:<a@x.test>\r\nRCPT TO:<lan@bluemail.test>\r\n", 3);
        (await Converse(s, "BDAT 5\r\nab.\r\n", 1)).Should().Contain("250 2.0.0 5 octets");
        (await Converse(s, "BDAT 3 LAST\r\n.\r\n", 1)).Should().Contain("Queued");
        await Converse(s, "QUIT\r\n", 1);
        await server;
        Encoding.ASCII.GetString(h.Messages.Single()).Should().Be("ab.\r\n.\r\n");
    }

    [Fact]
    public async Task Oversized_message_is_refused_after_the_terminator()
    {
        var (client, server, h) = await Start(maxBytes: 100);
        var s = client.GetStream();
        await Converse(s, "", 1);
        await Converse(s, "EHLO c.test\r\nMAIL FROM:<a@x.test>\r\nRCPT TO:<lan@bluemail.test>\r\nDATA\r\n", 4);
        (await Converse(s, new string('x', 500) + "\r\n.\r\n", 1)).Should().Contain("552");
        await Converse(s, "QUIT\r\n", 1);
        await server;
        h.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Long_command_line_is_500_and_session_continues()
    {
        var (client, server, _) = await Start();
        var s = client.GetStream();
        await Converse(s, "", 1);
        (await Converse(s, "NOOP " + new string('a', 10_000) + "\r\n", 1)).Should().StartWith("500");
        (await Converse(s, "NOOP\r\n", 1)).Should().StartWith("250");
        await Converse(s, "QUIT\r\n", 1);
        await server;
    }

    [Fact]
    public async Task Starttls_injection_plaintext_after_starttls_is_discarded()
    {
        using var cert = SelfSigned();
        var (client, server, _) = await Start(cert: cert);
        var s = client.GetStream();
        await Converse(s, "", 1);
        await Converse(s, "EHLO c.test\r\n", 1);
        // Attacker-injected command in the same packet as STARTTLS: must never be executed inside TLS.
        (await Converse(s, "STARTTLS\r\nMAIL FROM:<evil@x.test>\r\n", 1)).Should().StartWith("220");
        await using var tls = new SslStream(s, false, (_, presented, _, _) => presented?.GetCertHashString() == cert.GetCertHashString());
        await tls.AuthenticateAsClientAsync("mx.bluemail.test");
        // If the injected MAIL had been processed after the handshake, the next reply would be its 503 (no EHLO).
        (await Converse(tls, "NOOP\r\n", 1)).Should().StartWith("250 ");
        var ehlo = await Converse(tls, "EHLO c.test\r\n", 1);
        ehlo.Should().NotContain("STARTTLS");
        (await Converse(tls, "MAIL FROM:<a@x.test>\r\n", 1)).Should().StartWith("250", "state was reset by TLS, not polluted by the injected MAIL");
        await Converse(tls, "QUIT\r\n", 1);
        await server;
    }
}
