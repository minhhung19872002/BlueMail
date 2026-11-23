using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Rules;

/// <summary>
/// Runs "also apply to matching conversations" jobs (PROMPT 6.8) in the background: claimed with SKIP LOCKED and a
/// lease (several API instances are safe; a crashed one's job is taken over), applied in batches through the
/// mail store, progress visible to the user. Forwarding is never applied to existing mail.
/// </summary>
public sealed class FilterJobWorker(IServiceScopeFactory scopes, ILogger<FilterJobWorker> log) : BackgroundService
{
    public const int Batch = 200;
    public const int MaxMessages = 50_000;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Filter job loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>Claims and runs one job; false when there was none.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE mail.filter_jobs SET status = 'RUNNING', started_at = coalesce(started_at, now()), locked_until = now() + {Lease}
            WHERE id = (SELECT id FROM mail.filter_jobs
                        WHERE status = 'QUEUED' OR (status = 'RUNNING' AND locked_until < now())
                        ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id AS value
            """).ToListAsync(ct);
        if (claimed.Count == 0)
        {
            return false;
        }

        var job = await db.FilterJobs.IgnoreQueryFilters().AsNoTracking().FirstAsync(j => j.Id == claimed[0], ct);
        try
        {
            await RunAsync(scope.ServiceProvider, job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Filter job {Job} failed", job.Id);
            await db.FilterJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
                .SetProperty(x => x.Status, FilterJobStatus.Failed).SetProperty(x => x.Error, ex.GetType().Name).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
        }

        return true;
    }

    private static async Task RunAsync(IServiceProvider sp, FilterJob job, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var store = sp.GetRequiredService<PgMailStore>();
        var filter = await db.Filters.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(f => f.Id == job.FilterId && f.DeletedAt == null, ct);
        MailboxAccess access;
        try
        {
            access = await store.OpenAsync(job.TenantId, job.UserId, job.MailboxId, ct); // access is checked again: it may have been revoked
        }
        catch (MailboxAccessDeniedException)
        {
            filter = null;
            access = null!;
        }

        if (filter is null)
        {
            await Finish(db, job.Id, FilterJobStatus.Failed, "filter_gone", ct);
            return;
        }

        var owner = await new Mail.SearchContexts(db).ForMailboxAsync(job.TenantId, job.MailboxId, job.UserId, ct);
        var node = SearchParser.Parse(filter.Criteria, new SearchContext(owner.Zone, job.CreatedAt));
        if (node is null)
        {
            await Finish(db, job.Id, FilterJobStatus.Failed, "filter_no_criteria", ct);
            return;
        }

        var a = FilterActions.Parse(filter.Actions);
        var change = ChangeOf(a);

        // Matches are taken first (a snapshot), so actions that change what matches (archive, labels) cannot loop.
        var ids = new List<Guid>();
        (DateTimeOffset, Guid)? cursor = null;
        while (ids.Count < MaxMessages)
        {
            var page = await store.SearchMessagesAsync(access, node, owner.Addresses, cursor, 1000, ct);
            ids.AddRange(page.Select(p => p.Id));
            if (page.Count < 1000)
            {
                break;
            }

            cursor = (page[^1].At, page[^1].Id);
        }

        await db.FilterJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j.SetProperty(x => x.Total, ids.Count), ct);
        var done = 0;
        foreach (var chunk in ids.Chunk(Batch))
        {
            foreach (var c in change)
            {
                await store.ApplyAsync(access, chunk, c, ct);
            }

            done += chunk.Length;
            await db.FilterJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
                .SetProperty(x => x.Done, done).SetProperty(x => x.LockedUntil, DateTimeOffset.UtcNow + Lease), ct);
        }

        await Finish(db, job.Id, FilterJobStatus.Done, null, ct);
    }

    /// <summary>The store changes that make existing mail look as if the filter had run at delivery.</summary>
    public static IReadOnlyList<MessageChange> ChangeOf(FilterActions a)
    {
        var list = new List<MessageChange>();
        var add = new List<string>();
        var remove = new List<string>();
        if (a.Trash)
        {
            add.Add(SystemLabels.Trash);
            remove.AddRange([SystemLabels.Inbox, SystemLabels.Spam]);
        }
        else if (a.SkipInbox)
        {
            remove.Add(SystemLabels.Inbox);
        }

        if (a.Category is { } cat)
        {
            remove.AddRange([SystemLabels.CategoryPrimary, SystemLabels.CategorySocial, SystemLabels.CategoryPromotions, SystemLabels.CategoryUpdates,
                SystemLabels.CategoryForums]);
            remove.Remove("CATEGORY_" + cat);
            add.Add("CATEGORY_" + cat);
        }

        if (add.Count > 0 || remove.Count > 0 || a.MarkRead || a.Star || a.Important is not null)
        {
            list.Add(new MessageChange
            {
                AddSystem = add,
                RemoveSystem = remove,
                Seen = a.MarkRead ? true : null,
                Starred = a.Star ? true : null,
                Important = a.Important switch { "ALWAYS" => true, "NEVER" => false, _ => null },
            });
        }

        if (a.LabelIds.Count > 0)
        {
            list.Add(new MessageChange { AddLabels = a.LabelIds });
        }

        return list;
    }

    private static Task Finish(BlueMailDbContext db, Guid id, FilterJobStatus status, string? error, CancellationToken ct) =>
        db.FilterJobs.IgnoreQueryFilters().Where(j => j.Id == id).ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Status, status).SetProperty(x => x.Error, error).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
}
