using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlueMail.MailStore;

/// <summary>
/// Streaming authenticated encryption for stored messages (PROMPT 2.5): AES-256-GCM over 64 KiB chunks.
/// Format: header (magic "BMC1", chunk size, 12-byte base nonce, plaintext length) followed by chunks of
/// ciphertext||tag. Chunk i uses nonce = base XOR i and associated data = tenant id || content hash || i || last,
/// so chunks cannot be reordered, dropped, truncated or moved to another object. Only BCL cryptography is used.
/// Any chunk can be decrypted alone, which gives byte-range reads (one attachment) without fetching the message.
/// </summary>
public static class ChunkedAead
{
    public const int ChunkSize = 64 * 1024;
    public const int HeaderSize = 4 + 4 + 12 + 8;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "BMC1"u8.ToArray();

    public static long CiphertextLength(long plaintextLength) =>
        HeaderSize + plaintextLength + (Math.Max(1, (plaintextLength + ChunkSize - 1) / ChunkSize) * TagSize);

    public static async Task EncryptAsync(Stream plaintext, long length, Stream output, byte[] key, Guid tenantId, byte[] sha256, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), ChunkSize);
        var baseNonce = RandomNumberGenerator.GetBytes(12);
        baseNonce.CopyTo(header, 8);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(20), length);
        await output.WriteAsync(header, ct);

        using var gcm = new AesGcm(key, TagSize);
        var buf = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var chunks = Math.Max(1, (length + ChunkSize - 1) / ChunkSize);
        for (long i = 0; i < chunks; i++)
        {
            var n = (int)Math.Min(ChunkSize, length - (i * ChunkSize));
            await plaintext.ReadExactlyAsync(buf.AsMemory(0, n), ct);
            gcm.Encrypt(Nonce(baseNonce, i), buf.AsSpan(0, n), cipher.AsSpan(0, n), tag, Aad(tenantId, sha256, i, i == chunks - 1));
            await output.WriteAsync(cipher.AsMemory(0, n), ct);
            await output.WriteAsync(tag, ct);
        }
    }

    public sealed record Header(int ChunkSize, byte[] BaseNonce, long PlaintextLength)
    {
        public long Chunks => Math.Max(1, (PlaintextLength + ChunkSize - 1) / ChunkSize);

        /// <summary>Byte range of the ciphertext holding chunks [first, last].</summary>
        public (long Start, long End) CipherRange(long first, long last) =>
            (HeaderSize + (first * (ChunkSize + TagSize)), HeaderSize + Math.Min((last + 1) * (ChunkSize + TagSize), PlaintextLength + (Chunks * TagSize)) - 1);
    }

    public static Header ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic))
        {
            throw new CryptographicException("Not a BlueMail encrypted object.");
        }

        var chunk = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        var length = BinaryPrimitives.ReadInt64BigEndian(header[20..]);
        if (chunk is < 1024 or > 16 * 1024 * 1024 || length < 0)
        {
            throw new CryptographicException("Invalid object header.");
        }

        return new Header(chunk, header.Slice(8, 12).ToArray(), length);
    }

    /// <summary>Decrypts consecutive chunks starting at <paramref name="firstChunk"/> from <paramref name="cipher"/> (positioned at that chunk).</summary>
    public static async Task DecryptAsync(Stream cipher, Header h, long firstChunk, long lastChunk, Stream output, byte[] key, Guid tenantId, byte[] sha256,
        long skip, long take, CancellationToken ct)
    {
        using var gcm = new AesGcm(key, TagSize);
        var buf = new byte[h.ChunkSize + TagSize];
        var plain = new byte[h.ChunkSize];
        for (var i = firstChunk; i <= lastChunk && take > 0; i++)
        {
            var n = (int)Math.Min(h.ChunkSize, h.PlaintextLength - (i * h.ChunkSize));
            await cipher.ReadExactlyAsync(buf.AsMemory(0, n + TagSize), ct);
            gcm.Decrypt(Nonce(h.BaseNonce, i), buf.AsSpan(0, n), buf.AsSpan(n, TagSize), plain.AsSpan(0, n), Aad(tenantId, sha256, i, i == h.Chunks - 1));
            var from = (int)Math.Min(skip, n);
            var count = (int)Math.Min(n - from, take);
            await output.WriteAsync(plain.AsMemory(from, count), ct);
            skip = Math.Max(0, skip - n);
            take -= count;
        }
    }

    private static byte[] Nonce(byte[] baseNonce, long index)
    {
        var n = (byte[])baseNonce.Clone();
        Span<byte> idx = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(idx, index);
        for (var k = 0; k < 8; k++)
        {
            n[4 + k] ^= idx[k];
        }

        return n;
    }

    private static byte[] Aad(Guid tenantId, byte[] sha256, long index, bool last)
    {
        var aad = new byte[16 + 32 + 8 + 1];
        tenantId.TryWriteBytes(aad);
        sha256.CopyTo(aad, 16);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(48), index);
        aad[56] = last ? (byte)1 : (byte)0;
        return aad;
    }
}
