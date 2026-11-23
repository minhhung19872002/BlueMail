using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Users;

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserStatus Status,
    bool IsTenantAdmin,
    bool TwoFactorEnabled,
    long QuotaBytes,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

// ---------------- list ----------------

public sealed record ListUsersQuery(string? Search, int Page = 1, int PageSize = 50) : IRequest<Paged<UserDto>>;

public sealed class ListUsersHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListUsersQuery, Paged<UserDto>>
{
    public async Task<Paged<UserDto>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        me.Require(Permissions.AdminUsersView);
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = "%" + q.Search.Trim().ToLowerInvariant().Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(u => EF.Functions.ILike(u.PrimaryEmail, s) || EF.Functions.ILike(u.DisplayName, s));
        }

        // Count and page run on the same filtered set; order ends with the unique id (PROMPT 14.7).
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(u => new UserDto(u.Id, u.PrimaryEmail, u.DisplayName, u.Status, u.IsTenantAdmin, u.TwoFactorEnabled,
                u.QuotaBytes, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct);
        return new Paged<UserDto>(items, total, page, size);
    }
}

// ---------------- create ----------------

public sealed record CreateUserCommand(string Email, string DisplayName, string Password, bool IsTenantAdmin, long? QuotaBytes)
    : IRequest<UserDto>;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithErrorCode(ValidationCodes.Required)
            .Must(e => Domain.Common.EmailAddress.TryParse(e, out _)).WithErrorCode(ValidationCodes.InvalidEmail);
        RuleFor(x => x.DisplayName).NotEmpty().WithErrorCode(ValidationCodes.Required)
            .MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Password).NotEmpty().WithErrorCode(ValidationCodes.Required)
            .MinimumLength(UserProvisioning.MinPasswordLength).WithErrorCode(ValidationCodes.PasswordTooWeak);
        RuleFor(x => x.QuotaBytes).GreaterThan(0).When(x => x.QuotaBytes is not null).WithErrorCode(ValidationCodes.InvalidFormat);
    }
}

public sealed class CreateUserHandler(UserProvisioning provisioning, AuditWriter audit, ICurrentUser me)
    : IRequestHandler<CreateUserCommand, UserDto>
{
    public async Task<UserDto> Handle(CreateUserCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminUsersEdit);
        var user = await provisioning.CreateAsync(new NewUser(me.TenantId, c.Email, c.DisplayName, c.Password, c.IsTenantAdmin,
            QuotaBytes: c.QuotaBytes, MustChangePassword: true), ct);
        await audit.WriteAsync(new AuditEntry("user.create", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent,
            "user", user.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { email = user.PrimaryEmail, user.DisplayName, user.IsTenantAdmin })), ct);
        return new UserDto(user.Id, user.PrimaryEmail, user.DisplayName, user.Status, user.IsTenantAdmin, false, user.QuotaBytes, null, user.CreatedAt);
    }
}

// ---------------- suspend / reactivate ----------------

public sealed record SetUserStatusCommand(Guid UserId, UserStatus Status) : IRequest;

public sealed class SetUserStatusHandler(
    BlueMailDbContext db, UserManager<User> users, ISessionValidator sessions, AuditWriter audit, ICurrentUser me)
    : IRequestHandler<SetUserStatusCommand>
{
    public async Task Handle(SetUserStatusCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminUsersEdit);
        if (c.Status == UserStatus.Deleted)
        {
            throw AppException.BadRequest("use_delete_endpoint");
        }

        if (c.UserId == me.UserId)
        {
            throw AppException.BadRequest("cannot_change_own_status");
        }

        // Tenant filter applies: a user of another tenant is simply not found.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == c.UserId, ct) ?? throw AppException.NotFound("user_not_found");
        var old = user.Status;
        if (old == c.Status)
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        user.Status = c.Status;
        if (c.Status == UserStatus.Suspended)
        {
            // New stamp invalidates every token that carries the old one; revoke open sessions and app passwords' use too.
            await users.UpdateSecurityStampAsync(user);
            var now = DateTimeOffset.UtcNow;
            await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry(c.Status == UserStatus.Suspended ? "user.suspend" : "user.reactivate", AuditResult.Success,
            me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", user.Id.ToString(),
            JsonSerializer.Serialize(new { status = old.ToString() }), JsonSerializer.Serialize(new { status = c.Status.ToString() })), ct);
        await tx.CommitAsync(ct);
        await sessions.InvalidateUserAsync(user.Id);
    }
}
