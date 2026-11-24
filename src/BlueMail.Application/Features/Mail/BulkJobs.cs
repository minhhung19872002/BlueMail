using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Tenancy;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Mail;

public sealed record BulkJobDto(Guid Id, string Status, int Total, int Done, string? Error);

/// <summary>
/// "Select all N conversations" followed by an action (PROMPT 6.3, 8.3): more than one request may carry, so it runs
/// as a background job over the list or search the selection came from.
/// </summary>
public sealed record StartBulkJobCommand(Guid? MailboxId, string Action, Guid? LabelId, Guid? FromLabelId, DateTimeOffset? SnoozeUntil, Guid? ScopeLabelId,
    string? Query) : IRequest<BulkJobDto>;

public sealed class StartBulkJobValidator : AbstractValidator<StartBulkJobCommand>
{
    public StartBulkJobValidator()
    {
        RuleFor(x => x.Action).Must(MailActionNames.All.Contains).WithErrorCode("invalid_action");
        RuleFor(x => x.LabelId).NotNull().When(x => x.Action is MailActionNames.AddLabel or MailActionNames.RemoveLabel or MailActionNames.MoveTo);
        RuleFor(x => x.SnoozeUntil).NotNull().WithErrorCode("snooze_time_required")
            .Must(t => t > DateTimeOffset.UtcNow && t < DateTimeOffset.UtcNow.AddYears(5)).WithErrorCode("snooze_time_invalid")
            .When(x => x.Action == MailActionNames.Snooze);
        RuleFor(x => x.Query).MaximumLength(2000);
    }
}

public sealed class StartBulkJobHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<StartBulkJobCommand, BulkJobDto>
{
    /// <summary>Jobs one user may have waiting or running at once.</summary>
    public const int MaxActivePerUser = 3;

    public async Task<BulkJobDto> Handle(StartBulkJobCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        if ((access.Rights & MailboxRights.Write) == 0)
        {
            throw AppException.Forbidden("mailbox_read_only");
        }

        var active = await db.BulkMailJobs.CountAsync(j => j.UserId == access.ActorUserId
            && (j.Status == FilterJobStatus.Queued || j.Status == FilterJobStatus.Running), ct);
        if (active >= MaxActivePerUser)
        {
            throw AppException.BadRequest("too_many_jobs");
        }

        var job = new BulkMailJob
        {
            Id = Guid.NewGuid(), TenantId = access.TenantId, UserId = access.ActorUserId, MailboxId = access.MailboxId, Action = c.Action,
            LabelId = c.LabelId, FromLabelId = c.FromLabelId, SnoozeUntil = c.SnoozeUntil, ScopeLabelId = c.ScopeLabelId,
            Query = string.IsNullOrWhiteSpace(c.Query) ? null : c.Query.Trim(), Status = FilterJobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.BulkMailJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return BulkJobs.Dto(job);
    }
}

public sealed record GetBulkJobQuery(Guid Id) : IRequest<BulkJobDto>;

public sealed class GetBulkJobHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetBulkJobQuery, BulkJobDto>
{
    public async Task<BulkJobDto> Handle(GetBulkJobQuery q, CancellationToken ct) =>
        BulkJobs.Dto(await db.BulkMailJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.UserId == me.UserId, ct)
                     ?? throw AppException.NotFound("job_not_found"));
}

public static class BulkJobs
{
    public static BulkJobDto Dto(BulkMailJob j) => new(j.Id, EnumNames.ToUpperSnake(j.Status.ToString()), j.Total, j.Done, j.Error);
}

/// <summary>
/// Runs bulk mail jobs: claimed with SKIP LOCKED and a lease like FilterJobWorker. The matching conversations are
/// listed first (a snapshot, at most <see cref="MaxThreads"/>), then changed <see cref="MailActionValidator.MaxItems"/>
/// at a time through the same handler as a click, acting as the user who started the job (access checked again).
/// </summary>
public sealed class BulkMailJobWorker(IServiceScopeFactory scopes, ILogger<BulkMailJobWorker> log) : BackgroundService
{
    public const int MaxThreads = 50_000;
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
                log.LogError(ex, "Bulk job loop failed");
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
            UPDATE mail.bulk_jobs SET status = 'RUNNING', started_at = coalesce(started_at, now()), locked_until = now() + {Lease}
            WHERE id = (SELECT id FROM mail.bulk_jobs
                        WHERE status = 'QUEUED' OR (status = 'RUNNING' AND locked_until < now())
                        ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id AS value
            """).ToListAsync(ct);
        if (claimed.Count == 0)
        {
            return false;
        }

        var job = await db.BulkMailJobs.IgnoreQueryFilters().AsNoTracking().FirstAsync(j => j.Id == claimed[0], ct);
        try
        {
            await RunAsync(scope.ServiceProvider, job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Bulk job {Job} failed", job.Id);
            var code = ex is AppException app ? app.Code : ex.GetType().Name;
            await db.BulkMailJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
                .SetProperty(x => x.Status, FilterJobStatus.Failed).SetProperty(x => x.Error, code).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
        }

        return true;
    }

    private static async Task RunAsync(IServiceProvider sp, BulkMailJob job, CancellationToken ct)
    {
        sp.GetRequiredService<ITenantContext>().Set(job.TenantId, job.UserId);
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var store = sp.GetRequiredService<PgMailStore>();
        var opener = new MailboxOpener(sp.GetRequiredService<IMailStore>(), new JobUser(job.TenantId, job.UserId));
        var list = new ListConversationsHandler(opener, store, new SearchContexts(db));

        var threads = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await list.Handle(new ListConversationsQuery(job.MailboxId, job.ScopeLabelId, job.ScopeLabelId is null ? "all" : "label", false, true, cursor,
                PgMailStore.MaxPageSize, job.Query), ct);
            threads.AddRange(page.Items.Select(i => i.ThreadId));
            cursor = page.NextCursor;
        }
        while (cursor is not null && threads.Count < MaxThreads);

        threads = threads.Distinct().Take(MaxThreads).ToList();
        await db.BulkMailJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j.SetProperty(x => x.Total, threads.Count), ct);

        var action = new MailActionHandler(opener, store, sp.GetRequiredService<Antispam.SpamLearning>(), sp.GetRequiredService<Antispam.PhishingReports>(),
            sp.GetRequiredService<SnoozeService>());
        var done = 0;
        foreach (var chunk in threads.Chunk(MailActionValidator.MaxItems))
        {
            await action.Handle(new MailActionCommand(job.MailboxId, job.Action, chunk, [], job.LabelId, job.FromLabelId, job.SnoozeUntil), ct);
            done += chunk.Length;
            await db.BulkMailJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
                .SetProperty(x => x.Done, done).SetProperty(x => x.LockedUntil, DateTimeOffset.UtcNow + Lease), ct);
        }

        await db.BulkMailJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Status, FilterJobStatus.Done).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>The user who started the job, for the mailbox access check while it runs.</summary>
    private sealed class JobUser(Guid tenantId, Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public Guid TenantId => tenantId;
        public Guid SessionId => Guid.Empty;
        public string Email => "";
        public bool IsPlatformAdmin => false;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public string? Ip => null;
        public string? UserAgent => null;
    }
}
