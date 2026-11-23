using Microsoft.Extensions.Configuration;

namespace BlueMail.Infrastructure.Configuration;

/// <summary>
/// Process configuration from BM_* environment variables (PROMPT 0.1, 9). A missing required variable
/// stops the process with a message naming it.
/// </summary>
public sealed class BmSettings
{
    public required string DbConnection { get; init; }
    public required string MasterKeyBase64 { get; init; }
    public required string Valkey { get; init; }
    public required string S3Endpoint { get; init; }
    public required string S3AccessKey { get; init; }
    public required string S3SecretKey { get; init; }
    public string S3Region { get; init; } = "garage";

    /// <summary>Public URL of BlueID (OIDC issuer), e.g. https://id.bluemail.test.</summary>
    public required string IdentityUrl { get; init; }

    /// <summary>Public URL of the web app (OIDC redirect target), e.g. https://mail.bluemail.test.</summary>
    public required string WebUrl { get; init; }

    /// <summary>Host name used in SMTP/IMAP greetings and generated Message-IDs.</summary>
    public required string MailHostname { get; init; }

    /// <summary>CIDR ranges of reverse proxies whose X-Forwarded-For is trusted.</summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    public string LogDirectory { get; init; } = "logs";

    public static BmSettings Load(IConfiguration config)
    {
        string Required(string key)
        {
            var v = config[key];
            if (string.IsNullOrWhiteSpace(v))
            {
                throw new InvalidOperationException($"Required environment variable {key} is not set. See deploy/.env.example.");
            }

            return v;
        }

        return new BmSettings
        {
            DbConnection = Required("BM_DB_CONNECTION"),
            MasterKeyBase64 = Required("BM_MASTER_KEY"),
            Valkey = Required("BM_VALKEY"),
            S3Endpoint = Required("BM_S3_ENDPOINT"),
            S3AccessKey = Required("BM_S3_ACCESS_KEY"),
            S3SecretKey = Required("BM_S3_SECRET_KEY"),
            S3Region = config["BM_S3_REGION"] ?? "garage",
            IdentityUrl = Required("BM_IDENTITY_URL").TrimEnd('/'),
            WebUrl = Required("BM_WEB_URL").TrimEnd('/'),
            MailHostname = Required("BM_MAIL_HOSTNAME"),
            TrustedProxies = (config["BM_TRUSTED_PROXIES"] ?? "")
                .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LogDirectory = config["BM_LOG_DIR"] ?? "logs",
        };
    }
}
