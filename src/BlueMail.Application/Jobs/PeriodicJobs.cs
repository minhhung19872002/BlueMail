using BlueMail.Application.Features.Admin.Domains;
using BlueMail.Application.Features.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Jobs;

/// <summary>
/// Periodic business jobs run by Hangfire (PROMPT 1, 9; PostgreSQL storage, schema <c>hangfire</c>; docs/00 D35).
/// Each takes a distributed lock so two worker processes never run the same job at once, and each is safe to run
/// again after a crash. Queues that need low latency (bulk actions, filters, imports/exports, snoozes) and the
/// outbound queue keep their lease-based workers.
/// </summary>
public sealed class PeriodicJobs(IServiceScopeFactory scopes, ILogger<PeriodicJobs> log)
{
    public const string PurgeId = "mail-purge-trash-spam";
    public const string BlobsId = "mail-collect-blobs";
    public const string DomainsId = "domains-dns-check";

    /// <summary>Deletes forever what stayed 30 days in Trash or Spam (PROMPT 6.5; legal holds are respected by the store).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 2)]
    public async Task PurgeTrashAndSpamAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var total = 0;
        int n;
        do
        {
            n = await store.PurgeTrashAndSpamAsync(MailMaintenanceWorker.TrashRetention, 5000, ct);
            total += n;
        }
        while (n == 5000 && !ct.IsCancellationRequested);

        if (total > 0)
        {
            log.LogInformation("Deleted {Count} messages kept over 30 days in Trash or Spam", total);
        }
    }

    /// <summary>S3 objects nothing refers to any more (docs/00 D34).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 2)]
    public async Task CollectBlobsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<BlobStore>();
        while (await BlobRefs.CollectAsync(db, blobs, BlobRefs.Grace, 1000, log, ct) == 1000 && !ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>DNS of every domain not checked for <see cref="DomainWatchWorker.Interval"/> (PROMPT 7.2).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 1)]
    public Task CheckDomainsAsync(CancellationToken ct) =>
        scopes.CreateScope().ServiceProvider.GetRequiredService<DomainWatchWorker>().RunOnceAsync(DateTimeOffset.UtcNow - DomainWatchWorker.Interval, ct);
}

public static class JobsRegistration
{
    public const string Schema = "hangfire";

    /// <summary>Hangfire storage for every API role (the dashboard reads it); the server only where jobs run.</summary>
    public static IServiceCollection AddBlueMailHangfire(this IServiceCollection services, string connectionString, bool runServer)
    {
        services.AddSingleton<PeriodicJobs>();
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
            {
                SchemaName = Schema,
                PrepareSchemaIfNecessary = true,
                DistributedLockTimeout = TimeSpan.FromMinutes(10),
            }));
        if (runServer)
        {
            services.AddHangfireServer(o =>
            {
                o.WorkerCount = 2;
                o.ServerName = "bluemail-" + Environment.MachineName;
            });
        }

        return services;
    }

    /// <summary>The recurring jobs, registered (or updated) at start of a job-running process. Times are UTC.</summary>
    public static void RegisterRecurring(IRecurringJobManager jobs)
    {
        jobs.AddOrUpdate<PeriodicJobs>(PeriodicJobs.PurgeId, j => j.PurgeTrashAndSpamAsync(CancellationToken.None), "5 * * * *");
        jobs.AddOrUpdate<PeriodicJobs>(PeriodicJobs.BlobsId, j => j.CollectBlobsAsync(CancellationToken.None), "35 * * * *");
        jobs.AddOrUpdate<PeriodicJobs>(PeriodicJobs.DomainsId, j => j.CheckDomainsAsync(CancellationToken.None), "*/30 * * * *");
    }
}
