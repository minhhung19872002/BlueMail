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

    public WebApplicationFactory<SignInService> Identity { get; private set; } = null!;
    public WebApplicationFactory<BlueMail.Api.ApiAssemblyMarker> Api { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _valkey.StartAsync());
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
            ["BM_S3_ENDPOINT"] = "http://127.0.0.1:9",
            ["BM_S3_ACCESS_KEY"] = "test",
            ["BM_S3_SECRET_KEY"] = "test",
            ["BM_IDENTITY_URL"] = IdentityUrl,
            ["BM_WEB_URL"] = WebUrl,
            ["BM_MAIL_HOSTNAME"] = "mx.bluemail.test",
            ["BM_OIDC_SIGNING_KEY"] = signing,
            ["BM_OIDC_ENCRYPTION_KEY"] = encryption,
            ["BM_LOG_DIR"] = Path.Combine(Path.GetTempPath(), "bluemail-tests-" + Guid.NewGuid().ToString("N")),
            ["BM_PLATFORM_ADMIN_EMAIL"] = PlatformAdmin,
            ["BM_PLATFORM_ADMIN_PASSWORD"] = PlatformAdminPassword,
            ["BM_PLATFORM_DOMAIN"] = "platform.test",
            ["BM_SEED_SAMPLE_DATA"] = "true",
            ["BM_SEED_USER_PASSWORD"] = UserPassword,
        };
        foreach (var (k, v) in env)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        Identity = new WebApplicationFactory<SignInService>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        Api = new WebApplicationFactory<BlueMail.Api.ApiAssemblyMarker>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        _ = Identity.Server; // start: migrate + seed
        _ = Api.Server;
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Identity.DisposeAsync();
        await _pg.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    public AsyncServiceScope ApiScope() => Api.Services.CreateAsyncScope();
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}
