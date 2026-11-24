using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Mail;

/// <summary>
/// Snooze (PROMPT 6.5): the conversation leaves the Inbox for "Snoozed"; at the chosen time, or as soon as a new
/// message arrives in it, it comes back to the top of the Inbox, unread. One active snooze per conversation (DB rule).
/// </summary>
public sealed class SnoozeService(BlueMailDbContext db, PgMailStore store)
{
    public async Task SnoozeAsync(MailboxAccess access, IReadOnlyCollection<Guid> threadIds, DateTimeOffset until, CancellationToken ct)
    {
        foreach (var thread in threadIds.Distinct().Take(500))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Snoozes.IgnoreQueryFilters().Where(s => s.ThreadId == thread && s.UserId == access.ActorUserId && s.Status == SnoozeStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SnoozeStatus.Cancelled), ct);
            db.Snoozes.Add(new Snooze
            {
                Id = Guid.NewGuid(), TenantId = access.TenantId, UserId = access.ActorUserId, MailboxId = access.MailboxId, ThreadId = thread, UntilAt = until,
                Status = SnoozeStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }

        var ids = await store.ResolveMessagesAsync(access, threadIds, [], ct);
        await store.ApplyAsync(access, ids, new MessageChange { AddSystem = [SystemLabels.Snoozed], RemoveSystem = [SystemLabels.Inbox] }, ct);
    }

    /// <summary>Unsnooze now (by the user, at the time, or because new mail arrived): back in the Inbox, newest message unread.</summary>
    public async Task WakeAsync(MailboxAccess access, IReadOnlyCollection<Guid> threadIds, SnoozeStatus status, CancellationToken ct)
    {
        var n = await db.Snoozes.IgnoreQueryFilters()
            .Where(s => s.MailboxId == access.MailboxId && threadIds.Contains(s.ThreadId) && s.Status == SnoozeStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.WokenAt, DateTimeOffset.UtcNow), ct);
        if (n == 0)
        {
            return; // another worker or request already did it
        }

        var ids = await store.ResolveMessagesAsync(access, threadIds, [], ct);
        await store.ApplyAsync(access, ids, new MessageChange { RemoveSystem = [SystemLabels.Snoozed], AddSystem = [SystemLabels.Inbox] }, ct);
        var latest = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == access.TenantId && m.MailboxId == access.MailboxId && threadIds.Contains(m.ThreadId) && m.ExpungedAt == null && !m.Draft)
            .GroupBy(m => m.ThreadId).Select(g => g.OrderByDescending(m => m.ReceivedAt).Select(m => m.Id).First()).ToListAsync(ct);
        await store.ApplyAsync(access, latest, new MessageChange { Seen = false }, ct);
    }

    /// <summary>New mail in a snoozed conversation brings it back at once.</summary>
    public async Task WakeOnNewMailAsync(Guid tenant, Guid mailboxId, Guid threadId, CancellationToken ct)
    {
        if (await db.Snoozes.IgnoreQueryFilters().AnyAsync(s => s.MailboxId == mailboxId && s.ThreadId == threadId && s.Status == SnoozeStatus.Active, ct))
        {
            await WakeAsync(PgMailStore.SystemAccess(tenant, mailboxId), [threadId], SnoozeStatus.Woken, ct);
        }
    }

    public async Task<Dictionary<Guid, DateTimeOffset>> ActiveAsync(MailboxAccess access, IReadOnlyCollection<Guid> threadIds, CancellationToken ct) =>
        await db.Snoozes.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.MailboxId == access.MailboxId && threadIds.Contains(s.ThreadId) && s.Status == SnoozeStatus.Active)
            .ToDictionaryAsync(s => s.ThreadId, s => s.UntilAt, ct);
}

/// <summary>
/// Background mailbox housekeeping in the API process: wakes due snoozes (claimed with SKIP LOCKED so several
/// instances share the work), deletes forever what stayed 30 days in Trash or Spam (PROMPT 6.5) and collects unreferenced blobs.
/// </summary>
public sealed class MailMaintenanceWorker(IServiceScopeFactory scopes, ILogger<MailMaintenanceWorker> log) : BackgroundService
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WakeDueAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= _nextPurge)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var n = await scope.ServiceProvider.GetRequiredService<PgMailStore>().PurgeTrashAndSpamAsync(TrashRetention, 5000, stoppingToken);
                    if (n > 0)
                    {
                        log.LogInformation("Deleted {Count} messages kept over 30 days in Trash or Spam", n);
                    }

                    // S3 objects nothing refers to any more (expunged messages, sent queue rows, old photos).
                    await BlobRefs.CollectAsync(scope.ServiceProvider.GetRequiredService<BlueMailDbContext>(),
                        scope.ServiceProvider.GetRequiredService<BlobStore>(), BlobRefs.Grace, 1000, log, stoppingToken);
                    _nextPurge = DateTimeOffset.UtcNow.AddHours(1);
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Mail maintenance failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Wakes every snooze that is due; returns how many. Several instances may run this at once: the conditional
    /// status update in <see cref="SnoozeService.WakeAsync"/> lets exactly one of them act on each snooze.
    /// </summary>
    public async Task<int> WakeDueAsync(CancellationToken ct)
    {
        var woken = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            var due = await db.Snoozes.IgnoreQueryFilters().AsNoTracking().Where(s => s.Status == SnoozeStatus.Active && s.UntilAt <= DateTimeOffset.UtcNow)
                .OrderBy(s => s.UntilAt).Take(50).ToListAsync(ct);
            if (due.Count == 0)
            {
                return woken;
            }

            var service = scope.ServiceProvider.GetRequiredService<SnoozeService>();
            foreach (var s in due)
            {
                await service.WakeAsync(PgMailStore.SystemAccess(s.TenantId, s.MailboxId), [s.ThreadId], SnoozeStatus.Woken, ct);
                woken++;
            }
        }
    }
}

public sealed record SnoozedDto(Guid ThreadId, DateTimeOffset UntilAt);

public sealed record ListSnoozedQuery(Guid? MailboxId) : MediatR.IRequest<IReadOnlyList<SnoozedDto>>;

public sealed class ListSnoozedHandler(MailboxOpener opener, BlueMailDbContext db) : MediatR.IRequestHandler<ListSnoozedQuery, IReadOnlyList<SnoozedDto>>
{
    public async Task<IReadOnlyList<SnoozedDto>> Handle(ListSnoozedQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(q.MailboxId, ct);
        return await db.Snoozes.IgnoreQueryFilters().AsNoTracking().Where(s => s.MailboxId == access.MailboxId && s.Status == SnoozeStatus.Active)
            .OrderBy(s => s.UntilAt).Take(1000).Select(s => new SnoozedDto(s.ThreadId, s.UntilAt)).ToListAsync(ct);
    }
}
