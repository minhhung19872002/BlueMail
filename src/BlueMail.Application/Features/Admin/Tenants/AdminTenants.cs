using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Common;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Admin.Tenants;

public sealed record TenantDto(
    Guid Id, string Code, string Name, TenantKind Kind, TenantStatus Status, string Plan, int UserLimit,
    long StorageQuotaBytes, string DefaultLocale, string DefaultTimeZone, int Users, IReadOnlyList<string> Domains);

public sealed record NewTenant(
    string Code, string Name, string PrimaryDomain, string DefaultLocale, string DefaultTimeZone,
    int UserLimit = 0, long StorageQuotaBytes = 0, int OutboundDailyLimit = 0, TenantKind Kind = TenantKind.Customer,
    TenantStatus Status = TenantStatus.Active);

/// <summary>Creates a tenant with its wrapped data key and primary (not yet verified) domain.</summary>
public sealed class TenantProvisioning(BlueMailDbContext db, KeyService keys)
{
    public async Task<Tenant> CreateAsync(NewTenant n, CancellationToken ct)
    {
        string domain;
        try
        {
            domain = EmailAddress.NormalizeDomain(n.PrimaryDomain);
        }
        catch (ArgumentException)
        {
            throw new ValidationFailedException([new FieldError("primaryDomain", ValidationCodes.InvalidFormat)]);
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Code = n.Code.Trim().ToLowerInvariant(),
            Name = n.Name.Trim(),
            Kind = n.Kind,
            Status = n.Status,
            DefaultLocale = n.DefaultLocale,
            DefaultTimeZone = n.DefaultTimeZone,
            UserLimit = n.UserLimit,
            StorageQuotaBytes = n.StorageQuotaBytes,
            OutboundDailyLimit = n.OutboundDailyLimit,
        };
        tenant.EncryptionKeyWrapped = keys.NewWrappedTenantKey(tenant.Id);
        db.Tenants.Add(tenant);
        db.Domains.Add(new TenantDomain
        {
            TenantId = tenant.Id,
            Domain = domain,
            IsPrimary = true,
            VerificationToken = "bluemail-verify=" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            throw AppException.Conflict(pg.ConstraintName?.Contains("domain", StringComparison.Ordinal) == true ? "domain_taken" : "tenant_code_taken");
        }

        return tenant;
    }
}

public sealed record ListTenantsQuery(int Page = 1, int PageSize = 50) : IRequest<Paged<TenantDto>>;

public sealed class ListTenantsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListTenantsQuery, Paged<TenantDto>>
{
    public async Task<Paged<TenantDto>> Handle(ListTenantsQuery q, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsView);
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        // Platform view across tenants: the tenant filter is bypassed deliberately, guarded by the permission above.
        var query = db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.DeletedAt == null);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(t => t.Code).ThenBy(t => t.Id).Skip((page - 1) * size).Take(size)
            .Select(t => new TenantDto(t.Id, t.Code, t.Name, t.Kind, t.Status, t.Plan, t.UserLimit, t.StorageQuotaBytes,
                t.DefaultLocale, t.DefaultTimeZone,
                db.Users.IgnoreQueryFilters().Count(u => u.TenantId == t.Id && u.DeletedAt == null),
                db.Domains.IgnoreQueryFilters().Where(d => d.TenantId == t.Id && d.DeletedAt == null).OrderBy(d => d.Domain).Select(d => d.Domain).ToList()))
            .ToListAsync(ct);
        return new Paged<TenantDto>(items, total, page, size);
    }
}

public sealed record CreateTenantCommand(
    string Code, string Name, string PrimaryDomain, string DefaultLocale, string DefaultTimeZone, int UserLimit,
    long StorageQuotaBytes, string AdminEmail, string AdminDisplayName, string AdminPassword) : IRequest<Guid>;

public sealed class CreateTenantValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithErrorCode(ValidationCodes.Required)
            .Matches("^[a-z0-9][a-z0-9-]{1,62}$").WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.Name).NotEmpty().WithErrorCode(ValidationCodes.Required).MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.PrimaryDomain).NotEmpty().WithErrorCode(ValidationCodes.Required);
        RuleFor(x => x.DefaultTimeZone).Must(tz => TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.DefaultLocale).Matches("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$").WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.AdminEmail).Must(e => EmailAddress.TryParse(e, out _)).WithErrorCode(ValidationCodes.InvalidEmail);
        RuleFor(x => x.AdminDisplayName).NotEmpty().WithErrorCode(ValidationCodes.Required);
        RuleFor(x => x.AdminPassword).MinimumLength(UserProvisioning.MinPasswordLength).WithErrorCode(ValidationCodes.PasswordTooWeak);
    }
}

public sealed class CreateTenantHandler(BlueMailDbContext db, TenantProvisioning tenants, UserProvisioning users, AuditWriter audit, ICurrentUser me)
    : IRequestHandler<CreateTenantCommand, Guid>
{
    public async Task<Guid> Handle(CreateTenantCommand c, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = await tenants.CreateAsync(new NewTenant(c.Code, c.Name, c.PrimaryDomain, c.DefaultLocale, c.DefaultTimeZone,
            c.UserLimit, c.StorageQuotaBytes), ct);
        await users.CreateAsync(new NewUser(tenant.Id, c.AdminEmail, c.AdminDisplayName, c.AdminPassword, IsTenantAdmin: true), ct);
        await audit.WriteAsync(new AuditEntry("tenant.create", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent,
            "tenant", tenant.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { tenant.Code, tenant.Name, domain = c.PrimaryDomain })), ct);
        await tx.CommitAsync(ct);
        return tenant.Id;
    }
}

public sealed record SetTenantStatusCommand(Guid TenantId, TenantStatus Status) : IRequest;

public sealed class SetTenantStatusHandler(BlueMailDbContext db, ISessionValidator sessions, AuditWriter audit, ICurrentUser me)
    : IRequestHandler<SetTenantStatusCommand>
{
    public async Task Handle(SetTenantStatusCommand c, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == c.TenantId && t.DeletedAt == null, ct)
                     ?? throw AppException.NotFound("tenant_not_found");
        if (tenant.Kind == TenantKind.Platform)
        {
            throw AppException.BadRequest("cannot_suspend_platform_tenant");
        }

        var old = tenant.Status;
        tenant.Status = c.Status;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("tenant.status", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent,
            "tenant", tenant.Id.ToString(), JsonSerializer.Serialize(new { status = old.ToString() }),
            JsonSerializer.Serialize(new { status = c.Status.ToString() })), ct);

        // Sessions of every user of the tenant must re-check at once.
        var ids = await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenant.Id).Select(u => u.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await sessions.InvalidateUserAsync(id);
        }
    }
}
