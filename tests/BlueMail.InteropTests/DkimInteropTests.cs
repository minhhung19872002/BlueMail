using System.Net;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Dkim;
using BlueMail.Dns;
using BlueMail.Mime;

namespace BlueMail.InteropTests;

/// <summary>BlueMail.Dkim against dkimpy and OpenDKIM (PROMPT 11, Phase 2 acceptance).</summary>
public sealed class DkimInteropTests
{
    private static readonly string Out = Path.Combine(ToolsContainer.RepoRoot, "tests", "interop", "out", "dkim");
    private static readonly string KeyDir = Path.Combine(ToolsContainer.RepoRoot, "deploy", "dev", "dkim");
    private static readonly IPEndPoint HostDns = new(IPAddress.Loopback, 55353);

    private static DkimVerifier Verifier() => new(new DnsDkimKeyResolver(new DnsClient(new DnsClientOptions { Servers = [HostDns] })));

    private static RSA DevKey(string domain)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Path.Combine(KeyDir, domain + ".private.pem")));
        return rsa;
    }

    private static byte[] Sample(string id) => new MessageBuilder
    {
        From = new MailboxAddress("Nguyễn Văn An", "an@bluemail.test"),
        To = [new MailboxAddress("Bình", "binh@doitac.test")],
        Subject = "Kiểm tra DKIM – 署名テスト " + id,
        MessageId = id + "@bluemail.test",
        Date = DateTimeOffset.UtcNow,
        TextBody = "Dòng 1   với  khoảng trắng   \n\n\nDòng cuối\t \n\n\n",
        HtmlBody = "<p>Xin chào</p>",
    }.ToBytes();

    private static string Run(params string[] args)
    {
        var (code, stdout, stderr) = ToolsContainer.Exec(["python", "/work/tests/interop/dkim_tool.py", .. args]);
        code.Should().Be(0, stderr);
        return stdout.Trim();
    }

    private static string Write(string name, byte[] data)
    {
        Directory.CreateDirectory(Out);
        File.WriteAllBytes(Path.Combine(Out, name), data);
        return "/work/tests/interop/out/dkim/" + name;
    }

    [Theory]
    [InlineData(Canon.Relaxed, Canon.Relaxed)]
    [InlineData(Canon.Simple, Canon.Simple)]
    [InlineData(Canon.Relaxed, Canon.Simple)]
    public void Dkimpy_verifies_our_signature_and_detects_tampering(Canon header, Canon body)
    {
        var msg = Sample("ours-" + header + body);
        var signer = new DkimSigner("bluemail.test", "bm2026", DevKey("bluemail.test")) { HeaderCanon = header, BodyCanon = body };
        var signed = signer.Sign(new MemoryStream(msg)).Concat(msg).ToArray();
        Run("verify", Write($"ours-{header}-{body}.eml", signed)).Should().Be("pass");

        var tampered = (byte[])signed.Clone();
        tampered[^10] ^= 0x01; // one body byte
        Run("verify", Write($"ours-{header}-{body}-tampered.eml", tampered)).Should().Be("fail");
    }

    [Fact]
    public void Appending_a_second_From_breaks_our_oversigned_signature_for_dkimpy_too()
    {
        var msg = Sample("oversign");
        var signed = new DkimSigner("bluemail.test", "bm2026", DevKey("bluemail.test")).Sign(new MemoryStream(msg)).Concat(msg).ToArray();
        var attacked = Encoding.ASCII.GetBytes("From: ceo@bluemail.test\r\n").Concat(signed).ToArray();
        Run("verify", Write("oversign-attacked.eml", attacked)).Should().Be("fail");
    }

    [Theory]
    [InlineData("relaxed/relaxed")]
    [InlineData("simple/simple")]
    [InlineData("relaxed/simple")]
    public async Task We_verify_dkimpy_signatures_and_detect_tampering(string canon)
    {
        var name = "theirs-" + canon.Replace('/', '-');
        var input = Write(name + "-in.eml", Sample(name));
        Run("sign", input, "/work/tests/interop/out/dkim/" + name + ".eml", "doitac.test", "dt2026", "/work/deploy/dev/dkim/doitac.test.private.pem", canon);
        var signed = File.ReadAllBytes(Path.Combine(Out, name + ".eml"));

        var r = await Verifier().VerifyAsync(new MemoryStream(signed));
        r.Should().ContainSingle().Which.Status.Should().Be(DkimStatus.Pass, r[0].Reason);
        r[0].Domain.Should().Be("doitac.test");

        var tampered = (byte[])signed.Clone();
        var subject = Encoding.ASCII.GetBytes("Subject:");
        var at = signed.AsSpan().IndexOf(subject) + 20;
        tampered[at] ^= 0x02;
        (await Verifier().VerifyAsync(new MemoryStream(tampered)))[0].Status.Should().Be(DkimStatus.Fail);
    }

    [Fact]
    public async Task We_verify_messages_signed_by_OpenDKIM_through_Postfix()
    {
        var marker = "odk-" + Guid.NewGuid().ToString("N")[..8];
        var (code, stdout, stderr) = ToolsContainer.Exec("swaks", "--server", "10.77.0.30", "--from", "an@doitac.test", "--to", "lan@bluemail.test",
            "--header", "Subject: OpenDKIM " + marker, "--body", "Nội dung kiểm tra\r\n\r\n");
        code.Should().Be(0, stdout + stderr);

        string? path = null;
        for (var i = 0; i < 30 && path is null; i++)
        {
            Thread.Sleep(500);
            var (_, found, _) = ToolsContainer.Exec("sh", "-c", $"grep -l '{marker}' /sink/*.eml 2>/dev/null | head -1");
            path = string.IsNullOrWhiteSpace(found) ? null : found.Trim();
        }

        path.Should().NotBeNull("Postfix relays to mx.bluemail.test (phase-0 sink)");
        var (_, b64, _) = ToolsContainer.Exec("base64", "-w0", path!);
        var message = Convert.FromBase64String(b64.Trim());
        var r = await Verifier().VerifyAsync(new MemoryStream(message));
        r.Should().ContainSingle().Which.Should().Match<DkimResult>(x => x.Status == DkimStatus.Pass && x.Domain == "doitac.test" && x.KeyBits == 2048);
    }
}

/// <summary>ARC (RFC 8617) against dkimpy in both directions, including a two-hop chain.</summary>
public sealed class ArcInteropTests
{
    private static readonly string Out = Path.Combine(ToolsContainer.RepoRoot, "tests", "interop", "out", "dkim");

    private static ArcVerifier Verifier() => new(new DnsDkimKeyResolver(new DnsClient(new DnsClientOptions { Servers = [new IPEndPoint(IPAddress.Loopback, 55353)] })));

    private static RSA DevKey(string domain)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Path.Combine(ToolsContainer.RepoRoot, "deploy", "dev", "dkim", domain + ".private.pem")));
        return rsa;
    }

    private static byte[] Sample(string id) => new MessageBuilder
    {
        From = new MailboxAddress("An", "an@doitac.test"), To = [new MailboxAddress(null, "group@bluemail.test")],
        Subject = "ARC " + id, MessageId = id + "@doitac.test", Date = DateTimeOffset.UtcNow, TextBody = "Thư qua danh sách nhóm.\n",
    }.ToBytes();

    private static string Write(string name, byte[] data)
    {
        Directory.CreateDirectory(Out);
        File.WriteAllBytes(Path.Combine(Out, name), data);
        return "/work/tests/interop/out/dkim/" + name;
    }

    private static string Tool(params string[] args)
    {
        var (code, stdout, stderr) = ToolsContainer.Exec(["python", "/work/tests/interop/dkim_tool.py", .. args]);
        code.Should().Be(0, stderr);
        return stdout.Trim();
    }

    [Fact]
    public async Task Our_arc_chain_is_valid_for_dkimpy_and_for_us_over_two_hops()
    {
        var msg = Sample("ours");
        var hop1 = new ArcSigner("bluemail.test", "bm2026", DevKey("bluemail.test")).Sign(new MemoryStream(msg), "mx.bluemail.test", "spf=pass smtp.mailfrom=doitac.test", ArcStatus.None);
        var m1 = hop1.Concat(msg).ToArray();
        Tool("arc-verify", Write("arc-ours-1.eml", m1)).Should().Be("pass");
        (await Verifier().VerifyAsync(new MemoryStream(m1))).Status.Should().Be(ArcStatus.Pass);

        var hop2 = new ArcSigner("acme.test", "bm2026", DevKey("acme.test")).Sign(new MemoryStream(m1), "mx.acme.test", "arc=pass", ArcStatus.Pass);
        var m2 = hop2.Concat(m1).ToArray();
        Tool("arc-verify", Write("arc-ours-2.eml", m2)).Should().Be("pass");
        var r = await Verifier().VerifyAsync(new MemoryStream(m2));
        r.Status.Should().Be(ArcStatus.Pass, r.Reason);
        r.Instances.Should().Be(2);

        var tampered = (byte[])m2.Clone();
        tampered[^5] ^= 1;
        Tool("arc-verify", Write("arc-ours-tampered.eml", tampered)).Should().Be("fail");
        (await Verifier().VerifyAsync(new MemoryStream(tampered))).Status.Should().Be(ArcStatus.Fail);
    }

    [Fact]
    public async Task We_validate_a_dkimpy_arc_chain()
    {
        // dkimpy seals only when the message carries Authentication-Results from its own authserv-id.
        var withAr = Encoding.ASCII.GetBytes("Authentication-Results: mail.doitac.test; spf=pass smtp.mailfrom=doitac.test\r\n").Concat(Sample("theirs")).ToArray();
        var input = Write("arc-theirs-in.eml", withAr);
        Tool("arc-sign", input, "/work/tests/interop/out/dkim/arc-theirs.eml", "doitac.test", "dt2026", "/work/deploy/dev/dkim/doitac.test.private.pem", "mail.doitac.test");
        var r = await Verifier().VerifyAsync(new MemoryStream(File.ReadAllBytes(Path.Combine(Out, "arc-theirs.eml"))));
        r.Status.Should().Be(ArcStatus.Pass, r.Reason);
    }

    [Fact]
    public async Task Message_without_arc_is_none() =>
        (await Verifier().VerifyAsync(new MemoryStream(Sample("none")))).Status.Should().Be(ArcStatus.None);
}
