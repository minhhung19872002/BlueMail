using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Domain.Compliance;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Compliance;

/// <summary>
/// Background compliance work (PROMPT 8: long work outside HTTP): builds exports (claimed with a lease, like the other
/// job queues), applies DELETE_AFTER retention, removes for good what holds and KEEP_MIN no longer keep, and deletes
/// expired export files.
/// </summary>
public sealed class ComplianceWorker(IServiceScopeFactory scopes, ILogger<ComplianceWorker> log) : BackgroundService
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ExportLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _lastHousekeeping > HousekeepingEvery)
                {
                    _lastHousekeeping = DateTimeOffset.UtcNow;
                    await HousekeepingAsync(stoppingToken);
                }

                if (!await RunExportOnceAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Compliance loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    public async Task<bool> RunExportOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE compliance.data_exports SET status = 'RUNNING', started_at = coalesce(started_at, now()), locked_until = now() + {Lease}
            WHERE id = (SELECT id FROM compliance.data_exports
                        WHERE status = 'QUEUED' OR (status = 'RUNNING' AND locked_until < now())
                        ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id AS value
            """).ToListAsync(ct);
        if (claimed.Count == 0)
        {
            return false;
        }

        var export = await db.DataExports.IgnoreQueryFilters().AsNoTracking().FirstAsync(e => e.Id == claimed[0], ct);
        var path = Path.Combine(Path.GetTempPath(), "bm-export-" + export.Id.ToString("N") + ".zip");
        try
        {
            await BuildAsync(scope.ServiceProvider, export, path, ct);
            var exports = scope.ServiceProvider.GetRequiredService<ExportStore>();
            var chunks = await exports.PutFileAsync(export.TenantId, export.Id, path, ct);
            string sha;
            await using (var f = File.OpenRead(path))
            {
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            }

            var size = new FileInfo(path).Length;
            await db.DataExports.IgnoreQueryFilters().Where(e => e.Id == export.Id).ExecuteUpdateAsync(e => e
                .SetProperty(x => x.Status, JobStatus.Done).SetProperty(x => x.Chunks, chunks).SetProperty(x => x.SizeBytes, size).SetProperty(x => x.Sha256, sha)
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow).SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow + ExportLifetime)
                .SetProperty(x => x.LockedUntil, (DateTimeOffset?)null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Export {Export} failed", export.Id);
            await db.DataExports.IgnoreQueryFilters().Where(e => e.Id == export.Id).ExecuteUpdateAsync(e => e
                .SetProperty(x => x.Status, JobStatus.Failed).SetProperty(x => x.Error, "export_failed").SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
        }
        finally
        {
            File.Delete(path);
        }

        return true;
    }

    private static async Task BuildAsync(IServiceProvider sp, DataExport export, string path, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var builder = sp.GetRequiredService<ExportBuilder>();
        var done = 0;
        var lastBeat = DateTimeOffset.UtcNow;
        async Task Tick()
        {
            done++;
            if (DateTimeOffset.UtcNow - lastBeat > TimeSpan.FromSeconds(2))
            {
                lastBeat = DateTimeOffset.UtcNow;
                await db.DataExports.IgnoreQueryFilters().Where(e => e.Id == export.Id).ExecuteUpdateAsync(e => e
                    .SetProperty(x => x.Done, done).SetProperty(x => x.LockedUntil, DateTimeOffset.UtcNow + Lease), ct);
            }
        }

        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            switch (export.Kind)
            {
                case ExportKind.UserData:
                    await builder.UserDataAsync(zip, export.TenantId, export.SubjectUserId!.Value, "", Tick, ct);
                    break;
                case ExportKind.Tenant:
                    await builder.TenantAsync(zip, export.TenantId, Tick, ct);
                    break;
                case ExportKind.EDiscovery:
                    {
                        var scope = sp.GetRequiredService<ComplianceScope>();
                        var store = sp.GetRequiredService<PgMailStore>();
                        var node = await scope.ParseAsync(export.TenantId, export.Query, ct);
                        var boxes = JsonSerializer.Deserialize<List<Guid>>(export.Mailboxes) ?? [];
                        var hits = new List<ComplianceHit>();
                        (DateTimeOffset, Guid)? after = null;
                        while (hits.Count < ExportBuilder.MaxEDiscoveryMessages)
                        {
                            var page = await store.ComplianceSearchAsync(export.TenantId, boxes.Count > 0 ? boxes : null, node, null, includeHidden: true, after, 5000, ct);
                            if (page.Count == 0)
                            {
                                break;
                            }

                            hits.AddRange(page);
                            after = (page[^1].ReceivedAt, page[^1].MessageId);
                        }

                        await db.DataExports.IgnoreQueryFilters().Where(e => e.Id == export.Id).ExecuteUpdateAsync(e => e.SetProperty(x => x.Total, hits.Count), ct);
                        await builder.EDiscoveryAsync(zip, export, hits, Tick, ct);
                        break;
                    }
            }
        }

        await db.DataExports.IgnoreQueryFilters().Where(e => e.Id == export.Id).ExecuteUpdateAsync(e => e.SetProperty(x => x.Done, done), ct);
    }

    /// <summary>Retention, final removal of released mail, expired export files.</summary>
    public async Task HousekeepingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var store = sp.GetRequiredService<PgMailStore>();
        var compliance = sp.GetRequiredService<ComplianceScope>();

        foreach (var rule in await db.RetentionRules.IgnoreQueryFilters().AsNoTracking()
                     .Where(r => r.Action == RetentionAction.DeleteAfter && r.IsEnabled && r.DeletedAt == null).ToListAsync(ct))
        {
            var removed = 0;
            try
            {
                var node = await compliance.ParseAsync(rule.TenantId, rule.Query, ct);
                var mailboxes = await compliance.MailboxesAsync(rule.TenantId, null, rule.OrgUnitId, ct);
                var hits = await store.ComplianceSearchAsync(rule.TenantId, mailboxes, node, DateTimeOffset.UtcNow.AddDays(-rule.Days), includeHidden: false, null, 5000, ct);
                foreach (var g in hits.GroupBy(h => h.MailboxId))
                {
                    // The expunge itself keeps held messages and those inside KEEP_MIN (docs/00 D22).
                    removed += (await store.ApplyAsync(PgMailStore.SystemAccess(rule.TenantId, g.Key), g.Select(h => h.MessageId).ToList(),
                        new MessageChange { Expunge = true }, ct)).Messages;
                }
            }
            catch (Application.Common.AppException ex)
            {
                log.LogWarning("Retention rule {Rule} skipped: {Code}", rule.Id, ex.Code);
            }

            await db.RetentionRules.IgnoreQueryFilters().Where(r => r.Id == rule.Id).ExecuteUpdateAsync(r => r
                .SetProperty(x => x.LastRunAt, DateTimeOffset.UtcNow).SetProperty(x => x.LastRunDeleted, removed), ct);
        }

        await store.ExpungeReleasedAsync(20_000, ct);

        var exports = sp.GetRequiredService<ExportStore>();
        foreach (var e in await db.DataExports.IgnoreQueryFilters().Where(e => e.Status == JobStatus.Done && e.ExpiresAt < DateTimeOffset.UtcNow && e.Chunks > 0)
                     .Take(100).ToListAsync(ct))
        {
            await exports.DeleteAsync(e.TenantId, e.Id, e.Chunks, ct);
            e.Chunks = 0;
            await db.SaveChangesAsync(ct);
        }
    }
}
