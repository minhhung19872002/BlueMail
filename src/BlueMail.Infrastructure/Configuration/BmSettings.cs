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

    /// <summary>
    /// BM_DB_MAX_POOL caps this process's connections (Npgsql's default is 100 per process; several processes against
    /// one PostgreSQL could exhaust max_connections when 2,000 IMAP sessions wake at once, BUG-069). An explicit
    /// "Maximum Pool Size" in BM_DB_CONNECTION wins.
    /// </summary>
    public static string WithPoolSize(string connection, string? maxPool)
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        if (int.TryParse(maxPool, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0
            && !connection.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase) && !connection.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            b.MaxPoolSize = max;
        }

        return b.ConnectionString;
    }

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
            DbConnection = WithPoolSize(Required("BM_DB_CONNECTION"), config["BM_DB_MAX_POOL"]),
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
