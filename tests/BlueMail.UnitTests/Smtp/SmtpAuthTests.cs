using System.Net;
using System.Text;
using BlueMail.Protocols.Smtp;

namespace BlueMail.UnitTests.Smtp;

/// <summary>SMTP AUTH for submission (RFC 4954, RFC 4616, RFC 7628, RFC 6409).</summary>
public class SmtpAuthTests
{
    private static (SmtpServerSession Session, List<SaslCredentials> Seen) New(bool tls, Func<SaslCredentials, SaslResult>? decide = null)
    {
        var seen = new List<SaslCredentials>();
        var session = new SmtpServerSession(new SmtpServerOptions
        {
            Hostname = "smtp.bluemail.test", RequireAuthentication = true, StartTlsAvailable = !tls,
            Authenticate = (c, _) =>
            {
                seen.Add(c);
                return ValueTask.FromResult(decide?.Invoke(c) ?? (c.Secret == "right" ? new SaslResult(true, c.User) : SaslResult.Wrong));
            },
        }, new FakeHandler(), new SmtpEnvelope { RemoteAddress = IPAddress.Loopback, Tls = tls });
        return (session, seen);
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static async Task<SmtpReply> Say(SmtpServerSession s, string line) => (await s.HandleLineAsync(line, false, default)).Replies[^1];

    [Fact]
    public async Task Auth_is_offered_and_accepted_only_over_tls()
    {
        var (plain, _) = New(tls: false);
        var ehlo = await plain.HandleLineAsync("EHLO client", false, default);
        ehlo.Replies[0].Lines.Should().NotContain(l => l.StartsWith("AUTH", StringComparison.Ordinal));
        (await Say(plain, "AUTH PLAIN " + B64("\0u@x\0right"))).Code.Should().Be(538);
        (await Say(plain, "MAIL FROM:<u@x>")).Code.Should().Be(530, "submission refuses mail before authentication");

        var (tls, seen) = New(tls: true);
        (await tls.HandleLineAsync("EHLO client", false, default)).Replies[0].Lines.Should().Contain("AUTH PLAIN LOGIN XOAUTH2 OAUTHBEARER");
        (await Say(tls, "AUTH PLAIN " + B64("\0lan@bluemail.test\0right"))).Code.Should().Be(235);
        tls.Envelope.AuthenticatedUser.Should().Be("lan@bluemail.test");
        (await Say(tls, "AUTH PLAIN " + B64("\0lan@bluemail.test\0right"))).Code.Should().Be(503, "only once per session");
        seen.Single().Should().Be(new SaslCredentials("PLAIN", "lan@bluemail.test", "right", false));
    }

    [Fact]
    public async Task Login_mechanism_with_challenges_and_failures()
    {
        var (s, _) = New(tls: true);
        await s.HandleLineAsync("EHLO c", false, default);
        var r = await Say(s, "AUTH LOGIN");
        (r.Code, r.Lines[0]).Should().Be((334, "VXNlcm5hbWU6"));
        (await Say(s, B64("lan@bluemail.test"))).Lines[0].Should().Be("UGFzc3dvcmQ6");
        (await Say(s, B64("wrong"))).Code.Should().Be(535);
        (await Say(s, "AUTH LOGIN")).Code.Should().Be(334);
        (await Say(s, "*")).Code.Should().Be(501, "the client cancelled");
        (await Say(s, "AUTH CRAM-MD5")).Code.Should().Be(504);
        (await Say(s, "AUTH PLAIN !!!notbase64")).Code.Should().Be(501);
        (await Say(s, "AUTH PLAIN " + B64("other\0lan@bluemail.test\0right"))).Code.Should().Be(501, "acting as someone else (authzid) is refused");
    }

    [Fact]
    public async Task Bearer_tokens_and_their_error_challenge()
    {
        var (s, seen) = New(tls: true, c => c.IsBearerToken && c.Secret == "good-token" ? new SaslResult(true, "lan@bluemail.test") : SaslResult.Wrong);
        await s.HandleLineAsync("EHLO c", false, default);
        (await Say(s, "AUTH XOAUTH2 " + B64("user=lan@bluemail.test\u0001auth=Bearer bad\u0001\u0001"))).Code.Should().Be(334, "RFC 7628 error challenge");
        (await Say(s, "")).Code.Should().Be(535);
        (await Say(s, "AUTH OAUTHBEARER " + B64("n,a=lan@bluemail.test,\u0001host=smtp\u0001port=465\u0001auth=Bearer good-token\u0001\u0001"))).Code.Should().Be(235);
        seen.Should().HaveCount(2).And.OnlyContain(c => c.IsBearerToken);
        seen[1].User.Should().Be("lan@bluemail.test");
    }

    [Fact]
    public async Task Temporary_lock_is_454()
    {
        var (s, _) = New(tls: true, _ => SaslResult.TryLater);
        await s.HandleLineAsync("EHLO c", false, default);
        (await Say(s, "AUTH PLAIN " + B64("\0u@x\0right"))).Code.Should().Be(454);
    }
}
