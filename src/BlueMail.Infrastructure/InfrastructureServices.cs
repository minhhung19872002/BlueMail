using System.Globalization;
using Amazon.S3;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Health;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Debugging;
using Serilog.Formatting.Compact;
using Serilog.Sinks.PostgreSQL;
using StackExchange.Redis;

namespace BlueMail.Infrastructure;

public static class InfrastructureServices
{
    /// <summary>
    /// Every BlueMail process runs with the invariant culture (PROMPT 0.2, 12): user-facing formatting
    /// takes the culture of the request/user explicitly, never the server's.
    /// </summary>
    public static void UseInvariantProcessCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    /// <summary>Structured JSON logs to file and PostgreSQL (table logs.app_logs), with SelfLog enabled.</summary>
    public static void AddBlueMailLogging(this WebApplicationBuilder builder, BmSettings settings, string processName)
    {
        SelfLog.Enable(msg => Console.Error.WriteLine("[serilog-selflog] " + msg));
        var columns = new Dictionary<string, ColumnWriterBase>
        {
            ["occurred_at"] = new UtcTimestampColumnWriter(),
            ["level"] = new LevelColumnWriter(true, NpgsqlTypes.NpgsqlDbType.Varchar),
            ["message"] = new RenderedMessageColumnWriter(NpgsqlTypes.NpgsqlDbType.Text),
            ["exception"] = new ExceptionColumnWriter(NpgsqlTypes.NpgsqlDbType.Text),
            ["properties"] = new LogEventSerializedColumnWriter(NpgsqlTypes.NpgsqlDbType.Jsonb),
        };
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("process", processName)
            .WriteTo.Console(new CompactJsonFormatter())
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(settings.LogDirectory, processName + "-.json"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.PostgreSQL(settings.DbConnection, "app_logs", columns, schemaName: "logs",
                needAutoCreateTable: true,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning));
    }

    public static IServiceCollection AddBlueMailInfrastructure(this IServiceCollection services, BmSettings settings)
    {
        services.AddSingleton(settings);
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<BlueMailDbContext>(o => o
            .UseNpgsql(settings.DbConnection, npg => npg.MigrationsHistoryTable("__ef_migrations", "public"))
            .UseSnakeCaseNamingConvention()
            .UseOpenIddict<Guid>());

        services.AddSingleton(KeyService.FromBase64(settings.MasterKeyBase64));
        services.AddSingleton<TenantKeyProvider>();
        services.AddSingleton<SignedUrls>();
        services.AddSingleton<Net.SafeHttpFetcher>(_ => new Net.SafeHttpFetcher());
        services.AddSingleton<IPasswordHasher<User>, Argon2PasswordHasher>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<AuditChainVerifier>();

        services.AddMemoryCache();
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(settings.Valkey);
            options.AbortOnConnectFail = false;
            options.ClientName = "bluemail";
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton<ISessionValidator, SessionValidator>();

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(settings.S3AccessKey, settings.S3SecretKey, new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = settings.S3Region,
        }));

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<ValkeyHealthCheck>("valkey", tags: ["ready"])
            .AddCheck<S3HealthCheck>("s3", tags: ["ready"]);
        return services;
    }

    /// <summary>True when the process runs inside an integration test host (skips external sinks).</summary>
    public static bool IsTesting(this IHostEnvironment env) => env.EnvironmentName == "Testing";
}
