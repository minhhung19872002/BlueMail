using BlueMail.Application.Common;
using BlueMail.Domain.Contacts;
using BlueMail.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Contacts;

/// <summary>
/// Outlook "Share Contacts" / "Open Shared Contacts", within the organisation: an owner lets a colleague read their
/// contacts (never change them). Not shared looks like "not found".
/// </summary>
public static class ContactShares
{
    public const int MaxShares = 50;

    /// <summary>Whose contacts a list request reads: the caller's own, or a colleague's who shared them.</summary>
    public static async Task<Guid> ResolveAsync(BlueMailDbContext db, ICurrentUser me, Guid? owner, CancellationToken ct)
    {
        if (owner is null || owner == me.UserId)
        {
            return me.UserId;
        }

        var shared = await db.ContactShares.AsNoTracking().AnyAsync(s => s.OwnerUserId == owner && s.GranteeUserId == me.UserId, ct);
        return shared ? owner.Value : throw AppException.NotFound("contacts_not_shared");
    }
}

public sealed record ContactShareDto(Guid UserId, string Email, string Name);

public sealed record ContactSharesDto(IReadOnlyList<ContactShareDto> Mine, IReadOnlyList<ContactShareDto> WithMe);

public sealed record ListContactSharesQuery : IRequest<ContactSharesDto>;

public sealed class ListContactSharesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListContactSharesQuery, ContactSharesDto>
{
    public async Task<ContactSharesDto> Handle(ListContactSharesQuery q, CancellationToken ct)
    {
        var mine = await (from s in db.ContactShares.AsNoTracking()
                          join u in db.Users.AsNoTracking() on s.GranteeUserId equals u.Id
                          where s.OwnerUserId == me.UserId
                          orderby u.DisplayName
                          select new ContactShareDto(u.Id, u.PrimaryEmail, u.DisplayName)).ToListAsync(ct);
        var withMe = await (from s in db.ContactShares.AsNoTracking()
                            join u in db.Users.AsNoTracking() on s.OwnerUserId equals u.Id
                            where s.GranteeUserId == me.UserId && u.DeletedAt == null
                            orderby u.DisplayName
                            select new ContactShareDto(u.Id, u.PrimaryEmail, u.DisplayName)).ToListAsync(ct);
        return new ContactSharesDto(mine, withMe);
    }
}

public sealed record SetContactShareCommand(string Email, bool Shared) : IRequest<ContactSharesDto>;

public sealed class SetContactShareValidator : AbstractValidator<SetContactShareCommand>
{
    public SetContactShareValidator() => RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress();
}

public sealed class SetContactShareHandler(BlueMailDbContext db, ICurrentUser me, IMediator mediator) : IRequestHandler<SetContactShareCommand, ContactSharesDto>
{
    public async Task<ContactSharesDto> Handle(SetContactShareCommand c, CancellationToken ct)
    {
        var email = c.Email.Trim().ToLowerInvariant();
        // Same organisation only (tenant query filter); other tenants look like unknown addresses.
        var grantee = await db.Users.AsNoTracking().Where(u => u.PrimaryEmail == email && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                      ?? throw AppException.NotFound("user_not_found");
        if (grantee == me.UserId)
        {
            throw AppException.BadRequest("cannot_share_with_self");
        }

        var row = await db.ContactShares.FirstOrDefaultAsync(s => s.OwnerUserId == me.UserId && s.GranteeUserId == grantee, ct);
        if (!c.Shared)
        {
            if (row is not null)
            {
                row.DeletedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (row is null)
        {
            if (await db.ContactShares.CountAsync(s => s.OwnerUserId == me.UserId, ct) >= ContactShares.MaxShares)
            {
                throw AppException.BadRequest("too_many_shares");
            }

            db.ContactShares.Add(new ContactShare { Id = Guid.NewGuid(), TenantId = me.TenantId, OwnerUserId = me.UserId, GranteeUserId = grantee });
        }

        await db.SaveChangesAsync(ct);
        return await mediator.Send(new ListContactSharesQuery(), ct);
    }
}
