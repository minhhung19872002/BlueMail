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
    Guid? ThreadId, DateTimeOffset CreatedAt);

public static class TaskViews
{
    /// <summary>Outlook To Do smart lists: all open tasks, important, with a due date, completed.</summary>
    public static readonly string[] All = ["my", "important", "planned", "completed"];
    public const int MaxOpen = 2000;

    public static TaskDto Dto(TaskItem t) => new(t.Id, t.Title, t.Notes, t.DueDate, t.Important, t.CompletedAt, t.MessageId, t.ThreadId, t.CreatedAt);
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
        return rows.Select(TaskViews.Dto).ToList();
    }
}

public sealed record SaveTaskCommand(Guid? Id, string? Title, string? Notes, DateOnly? DueDate, bool Important, bool Completed, Guid? MessageId)
    : IRequest<TaskDto>;

public sealed class SaveTaskValidator : AbstractValidator<SaveTaskCommand>
{
    public SaveTaskValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500).When(x => x.MessageId is null || x.Id is not null);
        RuleFor(x => x.Title).MaximumLength(500);
        RuleFor(x => x.Notes).MaximumLength(8000);
        RuleFor(x => x.DueDate).Must(d => d is null || (d.Value.Year >= 2000 && d.Value.Year <= 2200)).WithErrorCode("invalid_date");
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
        t.CompletedAt = c.Completed ? t.CompletedAt ?? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return TaskViews.Dto(t);
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
