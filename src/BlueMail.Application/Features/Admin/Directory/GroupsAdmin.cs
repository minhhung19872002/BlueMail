using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Directory;

// ---------- groups / distribution lists (PROMPT 7.3) ----------

public sealed record GroupMemberDto(Guid Id, Guid? UserId, string Email, string? Name, string Role);

public sealed record GroupDto(Guid Id, string Email, string Name, string? Description, string WhoCanPost, bool AllowExternal, bool Moderated, IReadOnlyList<GroupMemberDto> Members);

public sealed record ListGroupsQuery : IRequest<IReadOnlyList<GroupDto>>;

public sealed class ListGroupsHandler(BlueMailDbContext db) : IRequestHandler<ListGroupsQuery, IReadOnlyList<GroupDto>>
{
    public async Task<IReadOnlyList<GroupDto>> Handle(ListGroupsQuery q, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var members = await (from m in db.GroupMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id into us
                             from u in us.DefaultIfEmpty()
                             select new { m.Id, m.GroupId, m.UserId, Email = u != null ? u.PrimaryEmail : m.ExternalEmail!, Name = u != null ? u.DisplayName : null, m.Role })
            .ToListAsync(ct);
        return groups.Select(g => new GroupDto(g.Id, g.Email, g.Name, g.Description, EnumNames.ToUpperSnake(g.WhoCanPost.ToString()), g.AllowExternal, g.Moderated,
            members.Where(m => m.GroupId == g.Id).OrderBy(m => m.Email)
                .Select(m => new GroupMemberDto(m.Id, m.UserId, m.Email, m.Name, EnumNames.ToUpperSnake(m.Role.ToString()))).ToList())).ToList();
    }
}

public sealed record SaveGroupCommand(Guid? Id, string Email, string Name, string? Description, string WhoCanPost, bool AllowExternal, bool Moderated) : IRequest<Guid>;

public sealed class SaveGroupValidator : AbstractValidator<SaveGroupCommand>
{
    public SaveGroupValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.WhoCanPost).Must(v => v is "ANYONE" or "DOMAIN" or "MEMBERS" or "MANAGERS").WithErrorCode("invalid_option");
    }
}

public sealed class SaveGroupHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SaveGroupCommand, Guid>
{
    public async Task<Guid> Handle(SaveGroupCommand c, CancellationToken ct)
    {
        Group g;
        if (c.Id is { } id)
        {
            g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound("group_not_found");
        }
        else
        {
            var email = await AddressRules.FreeOwnedAddressAsync(db, me.TenantId, c.Email, ct);
            g = db.Groups.Add(new Group { TenantId = me.TenantId, Email = email }).Entity;
        }

        g.Name = c.Name.Trim();
        g.Description = c.Description?.Trim();
        g.WhoCanPost = Enum.Parse<GroupPostPermission>(c.WhoCanPost, true);
        g.AllowExternal = c.AllowExternal;
        g.Moderated = c.Moderated;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("group.save", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "group", g.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(c)), ct);
        return g.Id;
    }
}

public sealed record DeleteGroupCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteGroupHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<DeleteGroupCommand, Unit>
{
    public async Task<Unit> Handle(DeleteGroupCommand c, CancellationToken ct)
    {
        if (await db.Groups.Where(g => g.Id == c.Id).ExecuteUpdateAsync(g => g.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct) == 0)
        {
            throw AppException.NotFound("group_not_found");
        }

        await db.GroupMembers.Where(m => m.GroupId == c.Id).ExecuteUpdateAsync(m => m.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct);
        await audit.WriteAsync(new AuditEntry("group.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "group", c.Id.ToString()), ct);
        return Unit.Value;
    }
}

/// <summary>Adds a member: an address of the organisation (user) or, outside it, an external address.</summary>
public sealed record AddGroupMemberCommand(Guid GroupId, string Email, string Role) : IRequest<Guid>;

public sealed class AddGroupMemberHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<AddGroupMemberCommand, Guid>
{
    public async Task<Guid> Handle(AddGroupMemberCommand c, CancellationToken ct)
    {
        _ = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == c.GroupId, ct) ?? throw AppException.NotFound("group_not_found");
        if (!Domain.Common.EmailAddress.TryParse(c.Email.Trim(), out var parsed))
        {
            throw new ValidationFailedException([new FieldError("email", ValidationCodes.InvalidEmail)]);
        }

        var email = parsed.Value.Value;
        var role = Enum.TryParse<GroupMemberRole>(c.Role, true, out var r) ? r : GroupMemberRole.Member;
        var userId = await db.Users.AsNoTracking().Where(u => u.PrimaryEmail == email && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                     ?? await db.UserAliases.AsNoTracking().Where(a => a.Email == email).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct);
        var ours = await db.Domains.AsNoTracking().AnyAsync(d => d.Domain == parsed.Value.Domain, ct);
        if (userId is null && ours)
        {
            throw AppException.BadRequest("member_unknown"); // an address of ours that is not a user (groups nest by user, not by address)
        }

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == c.GroupId && (userId != null ? m.UserId == userId : m.ExternalEmail == email), ct))
        {
            throw AppException.Conflict("member_exists");
        }

        var m = db.GroupMembers.Add(new GroupMember { TenantId = me.TenantId, GroupId = c.GroupId, UserId = userId, ExternalEmail = userId is null ? email : null, Role = role }).Entity;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("group.member.add", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "group", c.GroupId.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { email, role = role.ToString() })), ct);
        return m.Id;
    }
}

public sealed record RemoveGroupMemberCommand(Guid GroupId, Guid MemberId) : IRequest<Unit>;

public sealed class RemoveGroupMemberHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<RemoveGroupMemberCommand, Unit>
{
    public async Task<Unit> Handle(RemoveGroupMemberCommand c, CancellationToken ct)
    {
        if (await db.GroupMembers.Where(m => m.Id == c.MemberId && m.GroupId == c.GroupId).ExecuteUpdateAsync(m => m.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct) == 0)
        {
            throw AppException.NotFound("not_found");
        }

        await audit.WriteAsync(new AuditEntry("group.member.remove", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "group", c.GroupId.ToString(),
            JsonSerializer.Serialize(new { member = c.MemberId })), ct);
        return Unit.Value;
    }
}

// ---------- shared mailboxes (PROMPT 7.3: read, send as, send on behalf) ----------

public sealed record SharedMemberDto(Guid UserId, string Email, bool CanRead, bool CanSendAs, bool CanSendOnBehalf);

public sealed record SharedMailboxDto(Guid Id, string Email, string Name, IReadOnlyList<SharedMemberDto> Members);

public sealed record ListSharedMailboxesQuery : IRequest<IReadOnlyList<SharedMailboxDto>>;

public sealed class ListSharedMailboxesHandler(BlueMailDbContext db) : IRequestHandler<ListSharedMailboxesQuery, IReadOnlyList<SharedMailboxDto>>
{
    public async Task<IReadOnlyList<SharedMailboxDto>> Handle(ListSharedMailboxesQuery q, CancellationToken ct)
    {
        var boxes = await db.SharedMailboxes.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var members = await (from m in db.SharedMailboxMembers.AsNoTracking() join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             select new { m.SharedMailboxId, m.UserId, u.PrimaryEmail, m.CanRead, m.CanSendAs, m.CanSendOnBehalf }).ToListAsync(ct);
        return boxes.Select(b => new SharedMailboxDto(b.Id, b.Email, b.Name, members.Where(m => m.SharedMailboxId == b.Id)
            .Select(m => new SharedMemberDto(m.UserId, m.PrimaryEmail, m.CanRead, m.CanSendAs, m.CanSendOnBehalf)).ToList())).ToList();
    }
}

public sealed record CreateSharedMailboxCommand(string Email, string Name) : IRequest<Guid>;

public sealed class CreateSharedMailboxHandler(BlueMailDbContext db, IMailStore store, ICurrentUser me, AuditWriter audit) : IRequestHandler<CreateSharedMailboxCommand, Guid>
{
    public async Task<Guid> Handle(CreateSharedMailboxCommand c, CancellationToken ct)
    {
        var email = await AddressRules.FreeOwnedAddressAsync(db, me.TenantId, c.Email, ct);
        var s = db.SharedMailboxes.Add(new SharedMailbox { TenantId = me.TenantId, Email = email, Name = string.IsNullOrWhiteSpace(c.Name) ? email : c.Name.Trim() }).Entity;
        await db.SaveChangesAsync(ct);
        await store.ProvisionMailboxAsync(me.TenantId, s.Id, MailboxOwnerKind.Shared, ct);
        await audit.WriteAsync(new AuditEntry("shared.create", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "shared_mailbox", s.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { email })), ct);
        return s.Id;
    }
}

/// <summary>Grants (or with all rights false, removes) a user's access to a shared mailbox.</summary>
public sealed record SetSharedMemberCommand(Guid SharedMailboxId, Guid UserId, bool CanRead, bool CanSendAs, bool CanSendOnBehalf) : IRequest<Unit>;

public sealed class SetSharedMemberHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit, Infrastructure.Security.ISessionValidator sessions)
    : IRequestHandler<SetSharedMemberCommand, Unit>
{
    public async Task<Unit> Handle(SetSharedMemberCommand c, CancellationToken ct)
    {
        _ = await db.SharedMailboxes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == c.SharedMailboxId, ct) ?? throw AppException.NotFound("not_found");
        _ = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == c.UserId, ct) ?? throw AppException.NotFound("user_not_found");
        var m = await db.SharedMailboxMembers.FirstOrDefaultAsync(x => x.SharedMailboxId == c.SharedMailboxId && x.UserId == c.UserId, ct);
        if (!c.CanRead && !c.CanSendAs && !c.CanSendOnBehalf)
        {
            if (m is not null)
            {
                m.DeletedAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            m ??= db.SharedMailboxMembers.Add(new SharedMailboxMember { TenantId = me.TenantId, SharedMailboxId = c.SharedMailboxId, UserId = c.UserId }).Entity;
            (m.CanRead, m.CanSendAs, m.CanSendOnBehalf) = (c.CanRead, c.CanSendAs, c.CanSendOnBehalf);
        }

        await db.SaveChangesAsync(ct);
        await sessions.InvalidateUserAsync(c.UserId);
        await audit.WriteAsync(new AuditEntry("shared.member", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "shared_mailbox",
            c.SharedMailboxId.ToString(), NewValueJson: JsonSerializer.Serialize(c)), ct);
        return Unit.Value;
    }
}

// ---------- organisational units ----------

public sealed record OrgUnitDto(Guid Id, string Name, Guid? ParentId, int Users);

public sealed record ListOrgUnitsQuery : IRequest<IReadOnlyList<OrgUnitDto>>;

public sealed class ListOrgUnitsHandler(BlueMailDbContext db) : IRequestHandler<ListOrgUnitsQuery, IReadOnlyList<OrgUnitDto>>
{
    public async Task<IReadOnlyList<OrgUnitDto>> Handle(ListOrgUnitsQuery q, CancellationToken ct) =>
        await db.OrgUnits.AsNoTracking().OrderBy(o => o.Name)
            .Select(o => new OrgUnitDto(o.Id, o.Name, o.ParentId, db.Users.Count(u => u.OrgUnitId == o.Id && u.DeletedAt == null))).ToListAsync(ct);
}
