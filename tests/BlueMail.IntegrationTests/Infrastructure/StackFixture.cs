using System.Security.Cryptography;
using BlueMail.Identity.Services;
using BlueMail.Infrastructure.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BlueMail.IntegrationTests.Infrastructure;

/// <summary>
/// Real PostgreSQL 16 and Valkey in containers, BlueID and the API hosted in-process against them.
/// BlueID's start-up migrates the database and seeds the platform admin and the two sample tenants.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public const string IdentityUrl = "http://id.test";
    public const string WebUrl = "http://web.test";
    public const string PlatformAdmin = "admin@platform.test";
    public const string PlatformAdminPassword = "Platform-Admin-2026!";
    public const string UserPassword = "Sample-User-2026!";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16.16-alpine")
        .WithDatabase("bluemail")
        .WithUsername("bluemail")
        .WithPassword("bluemail-test")
        .Build();

    private readonly IContainer _valkey = new ContainerBuilder("valkey/valkey:8.1.12-alpine3.24")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    // Garage, the S3 server of the deployment (deploy/garage), configured like scripts/s3-init.sh.
    public const string S3AccessKey = "GK" + "00112233445566778899aabb";
    public const string S3SecretKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private readonly IContainer _s3 = new ContainerBuilder("dxflrs/garage:v2.5.0")
        .WithResourceMapping(File.ReadAllBytes(Path.Combine(RepoRoot(), "deploy", "garage", "garage.toml")), DotNet.Testcontainers.Builders.FilePath.Of("/etc/garage.toml"))
        .WithEnvironment("GARAGE_RPC_SECRET", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
        .WithEnvironment("GARAGE_ADMIN_TOKEN", "test-admin")
        .WithPortBinding(3900, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API server listening")) // image has no shell for port probes
        .Build();

    public WebApplicationFactory<SignInService> Identity { get; private set; } = null!;
    public WebApplicationFactory<BlueMail.Api.ApiAssemblyMarker> Api { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _valkey.StartAsync(), _s3.StartAsync());
        await InitGarageAsync();
        ConnectionString = _pg.GetConnectionString() + ";Include Error Detail=true";
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS pgcrypto;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var (signing, encryption) = OidcKeys.Generate();
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["BM_DB_CONNECTION"] = ConnectionString,
            ["BM_MASTER_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["BM_VALKEY"] = $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}",
            ["BM_S3_ENDPOINT"] = $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(3900)}",
            ["BM_S3_ACCESS_KEY"] = S3AccessKey,
            ["BM_S3_SECRET_KEY"] = S3SecretKey,
            ["BM_IDENTITY_URL"] = IdentityUrl,
            ["BM_WEB_URL"] = WebUrl,
            ["BM_MAIL_HOSTNAME"] = "mx.bluemail.test",
            ["BM_OIDC_SIGNING_KEY"] = signing,
            ["BM_OIDC_ENCRYPTION_KEY"] = encryption,
            ["BM_PSL_URL"] = "off", // tests never download; PublicSuffixFeedTests drive the feed directly
            ["BM_LOG_DIR"] = Path.Combine(Path.GetTempPath(), "bluemail-tests-" + Guid.NewGuid().ToString("N")),
            ["BM_PLATFORM_ADMIN_EMAIL"] = PlatformAdmin,
            ["BM_PLATFORM_ADMIN_PASSWORD"] = PlatformAdminPassword,
            ["BM_PLATFORM_DOMAIN"] = "platform.test",
            ["BM_SEED_SAMPLE_DATA"] = "true",
            ["BM_SEED_USER_PASSWORD"] = UserPassword,
            ["BM_DNS_SERVERS"] = "127.0.0.1:9", // nothing answers: the domain wizard uses FakeDomainDns below
            // Import tests read the dev Dovecot of doitac.test (deploy/docker-compose.dev.yml, 127.0.0.1:55993).
            ["BM_IMPORT_ALLOW_PRIVATE"] = "true",
            ["BM_IMPORT_PORTS"] = "993,55993",
            ["BM_IMPORT_TRUSTED_CERTS"] = Path.Combine(RepoRoot(), "deploy", "dev", "postfix", "tls", "cert.pem"),
        };
        foreach (var (k, v) in env)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        Identity = new WebApplicationFactory<SignInService>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        Api = new WebApplicationFactory<BlueMail.Api.ApiAssemblyMarker>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .ConfigureServices(s => s.AddSingleton<BlueMail.Application.Features.Admin.Domains.IDomainDns>(Dns)));
        _ = Identity.Server; // start: migrate + seed
        _ = Api.Server;
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Identity.DisposeAsync();
        await _pg.DisposeAsync();
        await _valkey.DisposeAsync();
        await _s3.DisposeAsync();
    }

    private async Task InitGarageAsync()
    {
        async Task<string> Garage(params string[] args)
        {
            var r = await _s3.ExecAsync(["/garage", .. args]);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException($"garage {string.Join(' ', args)}: {r.Stderr}");
            }

            return r.Stdout;
        }

        string status = "";
        for (var i = 0; i < 30; i++)
        {
            var r = await _s3.ExecAsync(["/garage", "status"]);
            if (r.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(r.Stdout, "(?m)^[0-9a-f]{16}"))
            {
                status = r.Stdout;
                break;
            }

            await Task.Delay(500);
        }

        var node = System.Text.RegularExpressions.Regex.Match(status, "(?m)^([0-9a-f]{16})").Groups[1].Value;
        await Garage("layout", "assign", "-z", "dc1", "-c", "1G", node);
        await Garage("layout", "apply", "--version", "1");
        await Garage("key", "import", "--yes", "-n", "bluemail", S3AccessKey, S3SecretKey);
        foreach (var bucket in new[] { "bm-messages", "bm-attachments-large", "bm-exports", "bm-backups" })
        {
            await Garage("bucket", "create", bucket);
            await Garage("bucket", "allow", "--read", "--write", "--owner", bucket, "--key", S3AccessKey);
        }
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlueMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public AsyncServiceScope ApiScope() => Api.Services.CreateAsyncScope();

    /// <summary>DNS as the domain wizard sees it in tests; tests add the records an administrator would publish.</summary>
    public FakeDomainDns Dns { get; } = new();
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}
