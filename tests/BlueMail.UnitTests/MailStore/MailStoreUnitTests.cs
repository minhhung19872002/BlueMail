using System.Security.Cryptography;
using BlueMail.MailStore;

namespace BlueMail.UnitTests.MailStore;

public class ChunkedAeadTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static async Task<(byte[] Cipher, byte[] Sha)> Encrypt(byte[] plain)
    {
        var sha = SHA256.HashData(plain);
        var output = new MemoryStream();
        await ChunkedAead.EncryptAsync(new MemoryStream(plain), plain.Length, output, Key, Tenant, sha, default);
        return (output.ToArray(), sha);
    }

    private static async Task<byte[]> Decrypt(byte[] cipher, byte[] sha, long skip = 0, long? take = null, Guid? tenant = null)
    {
        var h = ChunkedAead.ReadHeader(cipher);
        var len = take ?? h.PlaintextLength;
        var first = skip / h.ChunkSize;
        var last = Math.Max(first, (skip + Math.Max(len, 1) - 1) / h.ChunkSize);
        var (start, end) = h.CipherRange(first, last);
        var output = new MemoryStream();
        await ChunkedAead.DecryptAsync(new MemoryStream(cipher[(int)start..(int)(end + 1)]), h, first, last, output, Key, tenant ?? Tenant, sha,
            skip - (first * h.ChunkSize), len, default);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkedAead.ChunkSize)]
    [InlineData(ChunkedAead.ChunkSize + 1)]
    [InlineData((3 * ChunkedAead.ChunkSize) + 17)]
    public async Task Round_trip_and_length_formula(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var (cipher, sha) = await Encrypt(plain);
        cipher.Length.Should().Be((int)ChunkedAead.CiphertextLength(size));
        (await Decrypt(cipher, sha)).Should().Equal(plain);
    }

    [Fact]
    public async Task Range_read_crossing_chunk_boundary()
    {
        var plain = RandomNumberGenerator.GetBytes((2 * ChunkedAead.ChunkSize) + 100);
        var (cipher, sha) = await Encrypt(plain);
        var skip = ChunkedAead.ChunkSize - 10;
        (await Decrypt(cipher, sha, skip, 50)).Should().Equal(plain.AsSpan(skip, 50).ToArray());
    }

    [Fact]
    public async Task Tampering_reordering_truncation_and_wrong_tenant_are_detected()
    {
        var plain = RandomNumberGenerator.GetBytes((2 * ChunkedAead.ChunkSize) + 5);
        var (cipher, sha) = await Encrypt(plain);

        var flipped = (byte[])cipher.Clone();
        flipped[ChunkedAead.HeaderSize + 100] ^= 1;
        await FluentActions.Awaiting(() => Decrypt(flipped, sha)).Should().ThrowAsync<CryptographicException>();

        // Swap chunk 0 and chunk 1 (same size): the index is in nonce and AAD.
        var block = ChunkedAead.ChunkSize + 16;
        var swapped = (byte[])cipher.Clone();
        Array.Copy(cipher, ChunkedAead.HeaderSize, swapped, ChunkedAead.HeaderSize + block, block);
        Array.Copy(cipher, ChunkedAead.HeaderSize + block, swapped, ChunkedAead.HeaderSize, block);
        await FluentActions.Awaiting(() => Decrypt(swapped, sha)).Should().ThrowAsync<CryptographicException>();

        // Truncate to two full chunks and claim that length: the "last chunk" flag is authenticated.
        var truncated = cipher[..(ChunkedAead.HeaderSize + (2 * block))];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(truncated.AsSpan(20), 2L * ChunkedAead.ChunkSize);
        await FluentActions.Awaiting(() => Decrypt(truncated, sha)).Should().ThrowAsync<CryptographicException>();

        await FluentActions.Awaiting(() => Decrypt(cipher, sha, tenant: Guid.NewGuid())).Should().ThrowAsync<CryptographicException>();
    }
}

public class SubjectNormalizerTests
{
    [Theory]
    [InlineData("Re: Báo giá tháng 11", "báo giá tháng 11", true)]
    [InlineData("RE[2]: Fwd: TL: Báo giá", "báo giá", true)]
    [InlineData("Trả lời: Họp", "họp", true)]
    [InlineData("回复：合同", "合同", true)]
    [InlineData("AW: WG: Angebot", "angebot", true)]
    [InlineData("[team] Re: plan", "plan", true)]
    [InlineData("Báo giá", "báo giá", false)]
    [InlineData("Return policy", "return policy", false)]        // "Re" inside a word is not a prefix
    [InlineData("Meeting: 10am", "meeting: 10am", false)]         // a colon after a non-prefix word stays
    [InlineData("", "", false)]
    public void Normalizes(string subject, string expected, bool prefix)
    {
        SubjectNormalizer.Normalize(subject, out var had).Should().Be(expected);
        had.Should().Be(prefix);
    }
}

public class SearchTextTests
{
    [Fact]
    public void Cjk_and_thai_become_bigrams_latin_stays_words()
    {
        SearchText.Prepare("合同已经").Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().Equal("合同", "同已", "已经");
        SearchText.Prepare("Hello WORLD").Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().Equal("hello", "world");
        SearchText.Prepare("สวัสดี").Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(5);
    }

    [Fact]
    public void Clean_removes_nul_and_lone_surrogates()
    {
        SearchText.Clean("a\0b\uD800c").Should().Be("abc");
        SearchText.Clean("Tiếng Việt 😀").Should().Be("Tiếng Việt 😀");
    }

    [Fact]
    public void Snippet_never_cuts_a_grapheme()
    {
        var s = SearchText.Snippet(string.Concat(Enumerable.Repeat("👨‍👩‍👧", 5)), 3);
        s.Should().Be("👨‍👩‍👧👨‍👩‍👧👨‍👩‍👧");
    }
}

/// <summary>BUG-020: noncharacters (fuzz text #13501) made string.Normalize throw, so delivery failed forever.</summary>
public class NoncharacterRegressionTests
{
    [Theory]
    [InlineData("Re: Báo￾ giá")]
    [InlineData("￿﷐x")]
    [InlineData("a\U0001FFFEb")]
    public void Text_functions_never_throw(string s)
    {
        SubjectNormalizer.Normalize(s, out _).Should().NotContain("￾");
        SearchText.Prepare(s).Should().NotBeNull();
        SearchText.Clean(s).Should().NotContain("￾").And.NotContain("￿").And.NotContain("﷐");
    }

    [Fact]
    public void Message_with_noncharacter_subject_yields_facts()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("From: a@b.test\r\nSubject: Re: H￾ọp\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nb￾ody\r\n");
        var stream = new MemoryStream(raw);
        var facts = MessageFacts.Extract(BlueMail.Mime.MimeMessage.Parse(stream), stream);
        facts.NormalizedSubject.Should().Be("họp");
        facts.BodyText.Should().NotContain("￾");
    }
}
