using System.Security.Cryptography;
using System.Text;
using BlueMail.Dkim;
using BlueMail.Dns;

namespace BlueMail.UnitTests.Mime;

public class DkimCanonicalizationTests
{
    // RFC 6376 3.4.5 example.
    private const string Headers = "A: X\r\nB : Y\t\r\n\tZ  \r\n";
    private const string Body = " C \r\nD \t E\r\n\r\n\r\n";

    [Fact]
    public void Relaxed_header_matches_rfc_example()
    {
        var hs = RawMessage.ReadHeaders(new MemoryStream(Encoding.ASCII.GetBytes(Headers + "\r\n")));
        var s = string.Concat(hs.Select(h => Encoding.ASCII.GetString(Canonicalizer.Header(h, Canon.Relaxed))));
        s.Should().Be("a:X\r\nb:Y Z\r\n");
    }

    [Fact]
    public void Simple_header_is_byte_exact()
    {
        var hs = RawMessage.ReadHeaders(new MemoryStream(Encoding.ASCII.GetBytes(Headers + "\r\n")));
        string.Concat(hs.Select(h => Encoding.ASCII.GetString(Canonicalizer.Header(h, Canon.Simple)))).Should().Be(Headers);
    }

    [Theory]
    [InlineData(Canon.Relaxed, " C\r\nD E\r\n")]
    [InlineData(Canon.Simple, " C \r\nD \t E\r\n")]
    public void Body_matches_rfc_example_in_any_chunking(Canon c, string expected)
    {
        var want = SHA256.HashData(Encoding.ASCII.GetBytes(expected));
        BodyHasher.Compute(new MemoryStream(Encoding.ASCII.GetBytes(Body)), c, HashAlgorithmName.SHA256).Should().Equal(want);
        using var h = new BodyHasher(c, HashAlgorithmName.SHA256);
        foreach (var b in Encoding.ASCII.GetBytes(Body))
        {
            h.Append([b]);
        }

        h.Finish().Should().Equal(want);
    }

    [Fact]
    public void Empty_body_simple_is_crlf_relaxed_is_empty()
    {
        BodyHasher.Compute(new MemoryStream(), Canon.Simple, HashAlgorithmName.SHA256).Should().Equal(SHA256.HashData("\r\n"u8));
        BodyHasher.Compute(new MemoryStream(), Canon.Relaxed, HashAlgorithmName.SHA256).Should().Equal(SHA256.HashData(""u8));
    }

    [Fact]
    public void Removing_b_keeps_bh()
    {
        var raw = Encoding.ASCII.GetBytes("DKIM-Signature: v=1; bh=AAAA; b=SIGN\r\n\t ATURE; d=x");
        Encoding.ASCII.GetString(DkimSignature.RemoveBValue(raw)).Should().Be("DKIM-Signature: v=1; bh=AAAA; b=; d=x");
    }
}

public class DkimVerifierTests
{
    private sealed class Keys(Dictionary<string, string> records) : IDkimKeyResolver
    {
        public Task<(DnsStatus Status, IReadOnlyList<string> Records)> LookupAsync(string selector, string domain, CancellationToken ct) =>
            Task.FromResult(records.TryGetValue(selector + "." + domain, out var r)
                ? (DnsStatus.Ok, (IReadOnlyList<string>)[r])
                : (DnsStatus.NxDomain, (IReadOnlyList<string>)[]));
    }

    private static readonly RSA Key = RSA.Create(2048);

    private static string Record(RSA k, string extra = "") => "v=DKIM1; k=rsa; " + extra + "p=" + Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());

    private static byte[] Msg => "From: a@x.test\r\nTo: b@y.test\r\nSubject: hi\r\nDate: Mon, 23 Nov 2026 10:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray();

    private static byte[] Signed(DkimSigner signer)
    {
        var m = Msg;
        return signer.Sign(new MemoryStream(m)).Concat(m).ToArray();
    }

    private static async Task<DkimResult> Verify(byte[] msg, string record, DateTimeOffset? now = null) =>
        (await new DkimVerifier(new Keys(new() { ["s1.x.test"] = record })) { Clock = () => now ?? DateTimeOffset.UtcNow }
            .VerifyAsync(new MemoryStream(msg))).Single();

    [Fact]
    public async Task Round_trip_passes()
    {
        var r = await Verify(Signed(new DkimSigner("x.test", "s1", Key)), Record(Key));
        r.Status.Should().Be(DkimStatus.Pass);
        r.KeyBits.Should().Be(2048);
    }

    [Fact]
    public async Task Expired_signature_fails()
    {
        var signer = new DkimSigner("x.test", "s1", Key) { Clock = () => DateTimeOffset.UtcNow.AddDays(-10), Expiry = TimeSpan.FromDays(1) };
        (await Verify(Signed(signer), Record(Key))).Reason.Should().Be("signature expired");
    }

    [Fact]
    public async Task Revoked_and_wrong_service_keys_are_permerror()
    {
        (await Verify(Signed(new DkimSigner("x.test", "s1", Key)), "v=DKIM1; k=rsa; p=")).Reason.Should().Be("key revoked");
        (await Verify(Signed(new DkimSigner("x.test", "s1", Key)), Record(Key, "s=tlsrpt; "))).Status.Should().Be(DkimStatus.PermError);
    }

    [Fact]
    public async Task Keys_under_1024_bits_are_refused()
    {
        using var weak = RSA.Create(512);
        var msg = Encoding.ASCII.GetBytes("DKIM-Signature: v=1; a=rsa-sha256; d=x.test; s=s1; h=from; bh=AAAA; b=AAAA\r\n").Concat(Msg).ToArray();
        (await Verify(msg, Record(weak))).Reason.Should().StartWith("key too short");
    }

    [Fact]
    public async Task Rsa_sha1_fails_and_ed25519_is_neutral()
    {
        var sha1 = Encoding.ASCII.GetBytes("DKIM-Signature: v=1; a=rsa-sha1; d=x.test; s=s1; h=from; bh=AAAA; b=AAAA\r\n").Concat(Msg).ToArray();
        (await Verify(sha1, Record(Key))).Status.Should().Be(DkimStatus.Fail);
        var ed = Encoding.ASCII.GetBytes("DKIM-Signature: v=1; a=ed25519-sha256; d=x.test; s=s1; h=from; bh=AAAA; b=AAAA\r\n").Concat(Msg).ToArray();
        (await Verify(ed, Record(Key))).Status.Should().Be(DkimStatus.Neutral);
    }

    [Fact]
    public async Task Body_appended_after_l_prefix_fails_because_l_is_ignored()
    {
        // A genuine signature with l= covering the original body ("body\r\n" = 6 bytes).
        var headers = RawMessage.ReadHeaders(new MemoryStream(Msg));
        var bh = Convert.ToBase64String(BodyHasher.Compute(new MemoryStream("body\r\n"u8.ToArray()), Canon.Relaxed, HashAlgorithmName.SHA256, limit: 6));
        var unsigned = Encoding.ASCII.GetBytes($"DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed; d=x.test; s=s1; l=6; h=from:to:subject; bh={bh}; b=");
        var data = DkimSigner.SignedData(headers, ["from", "to", "subject"], Canon.Relaxed, unsigned);
        var b = Convert.ToBase64String(Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var signed = unsigned.Concat(Encoding.ASCII.GetBytes(b + "\r\n")).Concat(Msg).ToArray();

        (await Verify(signed, Record(Key))).Status.Should().Be(DkimStatus.Pass, "the unmodified message still verifies");
        var withExtra = signed.Concat("appended by attacker\r\n"u8.ToArray()).ToArray();
        (await Verify(withExtra, Record(Key))).Reason.Should().Be("body hash mismatch", "content beyond l= is not trusted");
    }

    [Fact]
    public void Signer_refuses_short_keys()
    {
        using var k = RSA.Create(1024);
        var act = () => new DkimSigner("x.test", "s1", k);
        act.Should().Throw<ArgumentException>();
    }
}
