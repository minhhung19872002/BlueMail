using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace BlueMail.Infrastructure.Security;

/// <summary>
/// Token keys of BlueID. BM_OIDC_SIGNING_KEY is an RSA private key (PEM, base64-encoded so it fits an env
/// variable); BlueID signs with it and the API validates with its public half, without a discovery
/// round-trip. BM_OIDC_ENCRYPTION_KEY (base64, 32 bytes) encrypts BlueID's own artefacts (authorization
/// codes, refresh tokens). Access tokens are signed JWTs, not encrypted, so resource servers can read them.
/// </summary>
public sealed class OidcKeys
{
    public const string ApiAudience = "bluemail-api";
    public const string WebClientId = "bluemail-web";

    // Custom claims carried by access tokens.
    public const string SessionClaim = "bm_sid";
    public const string StampClaim = "bm_stamp";
    public const string TenantClaim = "bm_tenant";

    private OidcKeys(RsaSecurityKey signing, SymmetricSecurityKey encryption)
    {
        Signing = signing;
        Encryption = encryption;
    }

    public RsaSecurityKey Signing { get; }
    public SymmetricSecurityKey Encryption { get; }

    public static OidcKeys Load(IConfiguration config)
    {
        var signingB64 = config["BM_OIDC_SIGNING_KEY"];
        var encryptionB64 = config["BM_OIDC_ENCRYPTION_KEY"];
        if (string.IsNullOrWhiteSpace(signingB64))
        {
            throw new InvalidOperationException("Required environment variable BM_OIDC_SIGNING_KEY is not set. See deploy/.env.example.");
        }

        if (string.IsNullOrWhiteSpace(encryptionB64))
        {
            throw new InvalidOperationException("Required environment variable BM_OIDC_ENCRYPTION_KEY is not set. See deploy/.env.example.");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(signingB64)));
        if (rsa.KeySize < 2048)
        {
            throw new InvalidOperationException("BM_OIDC_SIGNING_KEY must be an RSA key of at least 2048 bits.");
        }

        var parameters = rsa.ExportParameters(false);
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(parameters.Modulus!))[..16];
        var enc = Convert.FromBase64String(encryptionB64);
        if (enc.Length != 32)
        {
            throw new InvalidOperationException("BM_OIDC_ENCRYPTION_KEY must decode to exactly 32 bytes.");
        }

        return new OidcKeys(new RsaSecurityKey(rsa) { KeyId = kid }, new SymmetricSecurityKey(enc));
    }

    /// <summary>Generates values for a new installation (used by the install script and tests).</summary>
    public static (string SigningKeyB64, string EncryptionKeyB64) Generate()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        return (Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }
}
