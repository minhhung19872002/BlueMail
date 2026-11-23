using System.Security.Cryptography;

namespace BlueMail.Infrastructure.Security;

/// <summary>
/// Envelope encryption (PROMPT 2.5, 8.1). A 256-bit master key (BM_MASTER_KEY, base64) wraps one random
/// 256-bit data key per tenant; tenant data (mail content, DKIM private keys, TOTP secrets, IMAP source
/// passwords) is encrypted with the tenant key. AES-256-GCM from System.Security.Cryptography only.
/// Format of every ciphertext: nonce(12) || tag(16) || ciphertext. The associated data binds a
/// ciphertext to its owner and purpose, so a value copied to another row fails to decrypt.
/// </summary>
public sealed class KeyService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _masterKey;

    public KeyService(byte[] masterKey)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("BM_MASTER_KEY must decode to exactly 32 bytes.", nameof(masterKey));
        }

        _masterKey = masterKey;
    }

    public static KeyService FromBase64(string base64) => new(Convert.FromBase64String(base64));

    /// <summary>A key for one purpose derived from the master key (HKDF-SHA256, RFC 5869); never the master key itself.</summary>
    public byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey, 32, salt: [], info: System.Text.Encoding.UTF8.GetBytes("bluemail:" + purpose));

    public byte[] NewWrappedTenantKey(Guid tenantId) => Encrypt(_masterKey, RandomNumberGenerator.GetBytes(32), Aad("tenant-key", tenantId));

    public byte[] UnwrapTenantKey(Guid tenantId, byte[] wrapped) => Decrypt(_masterKey, wrapped, Aad("tenant-key", tenantId));

    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), associatedData);
        return output;
    }

    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> data, ReadOnlySpan<byte> associatedData)
    {
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }

        var plaintext = new byte[data.Length - NonceSize - TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(data[..NonceSize], data[(NonceSize + TagSize)..], data.Slice(NonceSize, TagSize), plaintext, associatedData);
        return plaintext;
    }

    public static byte[] Aad(string purpose, Guid owner)
    {
        var p = System.Text.Encoding.ASCII.GetBytes(purpose);
        var aad = new byte[p.Length + 16];
        p.CopyTo(aad, 0);
        owner.TryWriteBytes(aad.AsSpan(p.Length));
        return aad;
    }
}
