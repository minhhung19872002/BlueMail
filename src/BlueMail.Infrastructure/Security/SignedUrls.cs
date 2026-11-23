using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlueMail.Infrastructure.Security;

public enum SignedKind { Part, Zip, Raw, Image }

/// <summary>What a signed URL grants. Short-lived; bound to the user and mailbox that were authorised.</summary>
public sealed record SignedGrant(SignedKind Kind, Guid TenantId, Guid UserId, Guid MailboxId, Guid MessageId, string? Part, string? Url, bool Download, long Expires);

/// <summary>
/// Capability URLs for content that the browser fetches without the bearer token: inline images inside the
/// sandboxed message iframe, attachment downloads, and proxied remote images (PROMPT 6.3). Format:
/// base64url(json) "." base64url(HMAC-SHA256). The image proxy only fetches URLs that BlueMail itself signed,
/// so it can never be used as an open proxy.
/// </summary>
public sealed class SignedUrls(KeyService keys)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private readonly byte[] _key = keys.DeriveKey("signed-url");

    public string Sign(SignedGrant grant)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(grant);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(_key, payload));
    }

    public static SignedGrant Grant(SignedKind kind, Guid tenant, Guid user, Guid mailbox, Guid message, string? part = null, string? url = null, bool download = false) =>
        new(kind, tenant, user, mailbox, message, part, url, download, DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds());

    /// <summary>Null when malformed, tampered with or expired.</summary>
    public SignedGrant? Verify(string token, DateTimeOffset? now = null)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || token.Length > 8192)
        {
            return null;
        }

        try
        {
            var payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            var mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_key, payload)))
            {
                return null;
            }

            var grant = JsonSerializer.Deserialize<SignedGrant>(payload);
            return grant is null || grant.Expires < (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() ? null : grant;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
