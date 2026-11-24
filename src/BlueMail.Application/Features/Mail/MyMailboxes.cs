using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Mail;

/// <summary>A mailbox the user can open: their own, or a shared one they were granted (PROMPT 6.2 "hộp thư dùng chung được cấp").</summary>
public sealed record MyMailboxDto(Guid Id, string Email, string Name, string Kind, bool CanSendAs, bool CanSendOnBehalf);

public sealed record ListMyMailboxesQuery : IRequest<IReadOnlyList<MyMailboxDto>>;

public sealed class ListMyMailboxesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListMyMailboxesQuery, IReadOnlyList<MyMailboxDto>>
{
    public async Task<IReadOnlyList<MyMailboxDto>> Handle(ListMyMailboxesQuery q, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => new { u.PrimaryEmail, u.DisplayName }).FirstAsync(ct);
        var own = await db.Mailboxes.AsNoTracking().Where(m => m.OwnerId == me.UserId && m.OwnerKind == MailboxOwnerKind.User)
            .Select(m => m.Id).FirstAsync(ct);
        var list = new List<MyMailboxDto> { new(own, user.PrimaryEmail, user.DisplayName, "own", true, false) };
        // Only grants that let the user read (the same check MailboxOpener applies when the mailbox is opened).
        list.AddRange(await (from g in db.SharedMailboxMembers.AsNoTracking()
                             join s in db.SharedMailboxes.AsNoTracking() on g.SharedMailboxId equals s.Id
                             join m in db.Mailboxes.AsNoTracking() on s.Id equals m.OwnerId
                             where g.UserId == me.UserId && g.CanRead && m.OwnerKind == MailboxOwnerKind.Shared
                             orderby s.Name
                             select new MyMailboxDto(m.Id, s.Email, s.Name, "shared", g.CanSendAs, g.CanSendOnBehalf)).ToListAsync(ct));
        return list;
    }
}
