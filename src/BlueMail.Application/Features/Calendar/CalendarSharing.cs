using BlueMail.Application.Common;
using BlueMail.Domain.Calendar;
using BlueMail.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Calendar;

public sealed record CalendarShareDto(Guid UserId, string Email, string Name, string Permission);

public sealed record CalendarSharesDto(IReadOnlyList<CalendarShareDto> Mine, IReadOnlyList<CalendarShareDto> WithMe);

public static class CalendarPermissions
{
    public const string View = "VIEW", Edit = "EDIT", None = "NONE";
    public const int MaxShares = 50;

    /// <summary>
    /// Whose calendar a request works on, and whether the caller may: their own always; someone else's only with a
    /// share of at least <paramref name="need"/> (VIEW or EDIT). Not shared looks like "not found".
    /// </summary>
    public static async Task<Guid> ResolveAsync(BlueMailDbContext db, ICurrentUser me, Guid? owner, string need, CancellationToken ct)
    {
        if (owner is null || owner == me.UserId)
        {
            return me.UserId;
        }

        var permission = await db.CalendarShares.AsNoTracking()
            .Where(s => s.OwnerUserId == owner && s.GranteeUserId == me.UserId).Select(s => s.Permission).FirstOrDefaultAsync(ct);
        if (permission is null || (need == Edit && permission != Edit))
        {
            throw AppException.NotFound("calendar_not_shared");
        }

        return owner.Value;
    }
}

public sealed record ListCalendarSharesQuery : IRequest<CalendarSharesDto>;

public sealed class ListCalendarSharesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListCalendarSharesQuery, CalendarSharesDto>
{
    public async Task<CalendarSharesDto> Handle(ListCalendarSharesQuery q, CancellationToken ct)
    {
        var mine = await (from s in db.CalendarShares.AsNoTracking()
                          join u in db.Users.AsNoTracking() on s.GranteeUserId equals u.Id
                          where s.OwnerUserId == me.UserId
                          orderby u.DisplayName
                          select new CalendarShareDto(u.Id, u.PrimaryEmail, u.DisplayName, s.Permission)).ToListAsync(ct);
        var withMe = await (from s in db.CalendarShares.AsNoTracking()
                            join u in db.Users.AsNoTracking() on s.OwnerUserId equals u.Id
                            where s.GranteeUserId == me.UserId && u.DeletedAt == null
                            orderby u.DisplayName
                            select new CalendarShareDto(u.Id, u.PrimaryEmail, u.DisplayName, s.Permission)).ToListAsync(ct);
        return new CalendarSharesDto(mine, withMe);
    }
}

/// <summary>Shares the caller's calendar with a colleague (VIEW or EDIT), or stops sharing (NONE).</summary>
public sealed record SetCalendarShareCommand(string Email, string Permission) : IRequest<CalendarSharesDto>;

public sealed class SetCalendarShareValidator : AbstractValidator<SetCalendarShareCommand>
{
    public SetCalendarShareValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320);
        RuleFor(x => x.Permission).Must(p => p is CalendarPermissions.View or CalendarPermissions.Edit or CalendarPermissions.None).WithErrorCode("invalid_permission");
    }
}

public sealed class SetCalendarShareHandler(BlueMailDbContext db, ICurrentUser me, IMediator mediator) : IRequestHandler<SetCalendarShareCommand, CalendarSharesDto>
{
    public async Task<CalendarSharesDto> Handle(SetCalendarShareCommand c, CancellationToken ct)
    {
        var email = c.Email.Trim().ToLowerInvariant();
        // Same organisation only (the tenant query filter); unknown addresses look the same as other tenants.
        var grantee = await db.Users.AsNoTracking().Where(u => u.PrimaryEmail == email && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                      ?? throw AppException.NotFound("user_not_found");
        if (grantee == me.UserId)
        {
            throw AppException.BadRequest("cannot_share_with_self");
        }

        var row = await db.CalendarShares.FirstOrDefaultAsync(s => s.OwnerUserId == me.UserId && s.GranteeUserId == grantee, ct);
        if (c.Permission == CalendarPermissions.None)
        {
            if (row is not null)
            {
                row.DeletedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (row is null)
        {
            if (await db.CalendarShares.CountAsync(s => s.OwnerUserId == me.UserId, ct) >= CalendarPermissions.MaxShares)
            {
                throw AppException.BadRequest("too_many_shares");
            }

            db.CalendarShares.Add(new CalendarShare { Id = Guid.NewGuid(), TenantId = me.TenantId, OwnerUserId = me.UserId, GranteeUserId = grantee, Permission = c.Permission });
        }
        else
        {
            row.Permission = c.Permission;
        }

        await db.SaveChangesAsync(ct);
        return await mediator.Send(new ListCalendarSharesQuery(), ct);
    }
}
