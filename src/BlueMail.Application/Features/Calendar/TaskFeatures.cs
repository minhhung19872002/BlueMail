using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Calendar;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Calendar;

public sealed record TaskDto(Guid Id, string Title, string? Notes, DateOnly? DueDate, bool Important, DateTimeOffset? CompletedAt, Guid? MessageId,
    Guid? ThreadId, DateTimeOffset CreatedAt, string? Category = null, string? AssignedTo = null, string? AssignedBy = null, DateTimeOffset? ReminderAt = null);

public static class TaskViews
{
    /// <summary>Outlook To Do smart lists: all open tasks, important, with a due date, completed.</summary>
    public static readonly string[] All = ["my", "important", "planned", "completed"];
    public const int MaxOpen = 2000;

    public static TaskDto Dto(TaskItem t, IReadOnlyDictionary<Guid, string>? names = null) =>
        new(t.Id, t.Title, t.Notes, t.DueDate, t.Important, t.CompletedAt, t.MessageId, t.ThreadId, t.CreatedAt, t.Category,
            t.AssignedToId is { } to ? names?.GetValueOrDefault(to) : null, t.AssignedById is { } by ? names?.GetValueOrDefault(by) : null, t.ReminderAt);

    /// <summary>Display names (or addresses) of the people tasks were assigned to or by, in the user's organisation.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(BlueMailDbContext db, IEnumerable<TaskItem> tasks, CancellationToken ct)
    {
        var ids = tasks.SelectMany(t => new[] { t.AssignedToId, t.AssignedById }).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.PrimaryEmail }).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.PrimaryEmail : u.DisplayName);
    }
}

public sealed record ListTasksQuery(string View) : IRequest<IReadOnlyList<TaskDto>>;

public sealed class ListTasksValidator : AbstractValidator<ListTasksQuery>
{
    public ListTasksValidator() => RuleFor(x => x.View).Must(TaskViews.All.Contains).WithErrorCode("invalid_view");
}

public sealed class ListTasksHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListTasksQuery, IReadOnlyList<TaskDto>>
{
    public async Task<IReadOnlyList<TaskDto>> Handle(ListTasksQuery q, CancellationToken ct)
    {
        var mine = db.Tasks.AsNoTracking().Where(t => t.UserId == me.UserId);
        var rows = q.View switch
        {
            "completed" => await mine.Where(t => t.CompletedAt != null).OrderByDescending(t => t.CompletedAt).Take(500).ToListAsync(ct),
            "important" => await mine.Where(t => t.CompletedAt == null && t.Important).OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
                .ThenByDescending(t => t.CreatedAt).Take(TaskViews.MaxOpen).ToListAsync(ct),
            "planned" => await mine.Where(t => t.CompletedAt == null && t.DueDate != null).OrderBy(t => t.DueDate).ThenByDescending(t => t.CreatedAt)
                .Take(TaskViews.MaxOpen).ToListAsync(ct),
            _ => await mine.Where(t => t.CompletedAt == null).OrderByDescending(t => t.Important).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
                .ThenByDescending(t => t.CreatedAt).Take(TaskViews.MaxOpen).ToListAsync(ct),
        };
        var names = await TaskViews.NamesAsync(db, rows, ct);
        return rows.Select(t => TaskViews.Dto(t, names)).ToList();
    }
}

public sealed record SaveTaskCommand(Guid? Id, string? Title, string? Notes, DateOnly? DueDate, bool Important, bool Completed, Guid? MessageId,
    string? Category = null, DateTimeOffset? ReminderAt = null) : IRequest<TaskDto>;

public sealed class SaveTaskValidator : AbstractValidator<SaveTaskCommand>
{
    public SaveTaskValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500).When(x => x.MessageId is null || x.Id is not null);
        RuleFor(x => x.Title).MaximumLength(500);
        RuleFor(x => x.Notes).MaximumLength(8000);
        RuleFor(x => x.DueDate).Must(d => d is null || (d.Value.Year >= 2000 && d.Value.Year <= 2200)).WithErrorCode("invalid_date");
        RuleFor(x => x.Category).Must(c => c is null || CalendarLimits.Colors.Contains(c)).WithErrorCode("invalid_color");
        RuleFor(x => x.ReminderAt).Must(r => r is null || (r.Value.Year >= 2000 && r.Value.Year <= 2200)).WithErrorCode("invalid_date");
    }
}

public sealed class SaveTaskHandler(BlueMailDbContext db, ICurrentUser me, MailboxOpener opener, PgMailStore store) : IRequestHandler<SaveTaskCommand, TaskDto>
{
    public async Task<TaskDto> Handle(SaveTaskCommand c, CancellationToken ct)
    {
        TaskItem t;
        if (c.Id is { } id)
        {
            t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("task_not_found");
        }
        else
        {
            var open = await db.Tasks.CountAsync(x => x.UserId == me.UserId && x.CompletedAt == null, ct);
            if (open >= TaskViews.MaxOpen)
            {
                throw AppException.BadRequest("too_many_tasks");
            }

            t = new TaskItem { Id = Guid.NewGuid(), TenantId = me.TenantId, UserId = me.UserId };
            if (c.MessageId is { } messageId)
            {
                // "Add to To Do" from a message of the user's own mailbox: the subject is the default title.
                var access = await opener.OpenAsync(null, ct);
                var m = await store.GetMessageAsync(access, messageId, ct) ?? throw AppException.NotFound("message_not_found");
                t.MessageId = m.Id;
                t.ThreadId = m.ThreadId;
                t.Title = string.IsNullOrWhiteSpace(c.Title) ? (string.IsNullOrWhiteSpace(m.Subject) ? "(?)" : m.Subject!.Trim()) : "";
            }

            db.Tasks.Add(t);
        }

        if (!string.IsNullOrWhiteSpace(c.Title))
        {
            t.Title = c.Title.Trim();
        }

        t.Title = t.Title.Length > 500 ? t.Title[..500] : t.Title;
        t.Notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim();
        t.DueDate = c.DueDate;
        t.Important = c.Important;
        t.Category = c.Category;
        t.ReminderAt = c.ReminderAt?.ToUniversalTime();
        t.CompletedAt = c.Completed ? t.CompletedAt ?? DateTimeOffset.UtcNow : null;
        if (t.OriginTaskId is { } origin)
        {
            // An assigned task reports its progress to the assigner's copy (Outlook "status report").
            var theirs = await db.Tasks.FirstOrDefaultAsync(x => x.Id == origin && x.AssignedToId == me.UserId, ct);
            if (theirs is not null)
            {
                theirs.CompletedAt = t.CompletedAt;
            }
        }

        await db.SaveChangesAsync(ct);
        return TaskViews.Dto(t, await TaskViews.NamesAsync(db, [t], ct));
    }
}

public sealed record DeleteTaskCommand(Guid Id) : IRequest<bool>;

public sealed class DeleteTaskHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteTaskCommand, bool>
{
    public async Task<bool> Handle(DeleteTaskCommand c, CancellationToken ct)
    {
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == c.Id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("task_not_found");
        t.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// Outlook "Assign task", within the organisation: the assignee gets their own copy in To Do; the assigner keeps the task
/// (marked with the assignee) and it completes when the assignee completes theirs.
/// </summary>
public sealed record AssignTaskCommand(Guid Id, string Email) : IRequest<TaskDto>;

public sealed class AssignTaskValidator : AbstractValidator<AssignTaskCommand>
{
    public AssignTaskValidator() => RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress();
}

public sealed class AssignTaskHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<AssignTaskCommand, TaskDto>
{
    public async Task<TaskDto> Handle(AssignTaskCommand c, CancellationToken ct)
    {
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == c.Id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("task_not_found");
        if (t.AssignedToId is not null || t.CompletedAt is not null)
        {
            throw AppException.BadRequest("task_not_assignable");
        }

        var email = c.Email.Trim().ToLowerInvariant();
        // Same organisation only (tenant query filter); other tenants look like unknown addresses.
        var assignee = await db.Users.AsNoTracking().Where(u => u.PrimaryEmail == email && u.DeletedAt == null && u.Status == BlueMail.Domain.Identity.UserStatus.Active)
                           .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                       ?? throw AppException.NotFound("user_not_found");
        if (assignee == me.UserId)
        {
            throw AppException.BadRequest("cannot_assign_to_self");
        }

        if (await db.Tasks.CountAsync(x => x.UserId == assignee && x.CompletedAt == null, ct) >= TaskViews.MaxOpen)
        {
            throw AppException.BadRequest("too_many_tasks");
        }

        db.Tasks.Add(new TaskItem
        {
            Id = Guid.NewGuid(), TenantId = me.TenantId, UserId = assignee, Title = t.Title, Notes = t.Notes, DueDate = t.DueDate,
            Important = t.Important, Category = t.Category, AssignedById = me.UserId, OriginTaskId = t.Id,
        });
        t.AssignedToId = assignee;
        await db.SaveChangesAsync(ct);
        return TaskViews.Dto(t, await TaskViews.NamesAsync(db, [t], ct));
    }
}
