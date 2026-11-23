using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Common;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Tenants;

public sealed record TenantDetailDto(Guid Id, string Code, string Name, string Status, string Plan, int UserLimit, long StorageQuotaBytes, int OutboundDailyLimit,
    string DefaultLocale, string DefaultTimeZone, bool SuspendTemporary, string Branding, TenantUsageDto Usage);

public sealed record TenantUsageDto(int Users, long StorageBytes, int Received30d, int Sent30d, IReadOnlyList<string> Domains);

public sealed record TenantUserDto(Guid Id, string Email, string DisplayName);

/// <summary>A customer's users, for choosing whom to help in a support sign-in (platform scope, not the caller's tenant).</summary>
public sealed record ListTenantUsersQuery(Guid TenantId, string? Search) : IRequest<IReadOnlyList<TenantUserDto>>;

public sealed class ListTenantUsersHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListTenantUsersQuery, IReadOnlyList<TenantUserDto>>
{
    public async Task<IReadOnlyList<TenantUserDto>> Handle(ListTenantUsersQuery q, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        var users = db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == q.TenantId && u.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = "%" + q.Search.Trim().Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            users = users.Where(u => EF.Functions.ILike(u.PrimaryEmail, s) || EF.Functions.ILike(u.DisplayName, s));
        }

        return await users.OrderBy(u => u.PrimaryEmail).Take(200).Select(u => new TenantUserDto(u.Id, u.PrimaryEmail, u.DisplayName)).ToListAsync(ct);
    }
}

public sealed record GetTenantQuery(Guid Id) : IRequest<TenantDetailDto>;

public sealed class GetTenantHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetTenantQuery, TenantDetailDto>
{
    public async Task<TenantDetailDto> Handle(GetTenantQuery q, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsView);
        var t = await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.DeletedAt == null, ct) ?? throw AppException.NotFound("tenant_not_found");
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var usage = new TenantUsageDto(
            await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == t.Id && u.DeletedAt == null, ct),
            await db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == t.Id && m.ExpungedAt == null && m.DeletedAt == null).SumAsync(m => (long?)m.Size, ct) ?? 0,
            await db.MailLog.IgnoreQueryFilters().CountAsync(l => l.TenantId == t.Id && l.OccurredAt >= since && l.Direction == Domain.Admin.MailDirection.Inbound
                                                                 && l.Status == Domain.Admin.MailLogStatus.Delivered, ct),
            await db.MailLog.IgnoreQueryFilters().CountAsync(l => l.TenantId == t.Id && l.OccurredAt >= since && l.Direction == Domain.Admin.MailDirection.Outbound
                                                                 && l.Status == Domain.Admin.MailLogStatus.Delivered, ct),
            await db.Domains.IgnoreQueryFilters().Where(d => d.TenantId == t.Id && d.DeletedAt == null).OrderBy(d => d.Domain).Select(d => d.Domain).ToListAsync(ct));
        return new TenantDetailDto(t.Id, t.Code, t.Name, EnumNames.ToUpperSnake(t.Status.ToString()), t.Plan, t.UserLimit, t.StorageQuotaBytes, t.OutboundDailyLimit,
            t.DefaultLocale, t.DefaultTimeZone, t.SuspendTemporary, t.Branding, usage);
    }
}

public sealed record UpdateTenantCommand(Guid Id, string Name, string Plan, int UserLimit, long StorageQuotaBytes, int OutboundDailyLimit, string DefaultLocale,
    string DefaultTimeZone, bool SuspendTemporary, string Branding) : IRequest<Unit>;

public sealed class UpdateTenantValidator : AbstractValidator<UpdateTenantCommand>
{
    public UpdateTenantValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Plan).NotEmpty().MaximumLength(50);
        RuleFor(x => x.UserLimit).GreaterThanOrEqualTo(0);
        RuleFor(x => x.StorageQuotaBytes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.OutboundDailyLimit).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DefaultTimeZone).Must(tz => TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithErrorCode(ValidationCodes.InvalidFormat);
        RuleFor(x => x.DefaultLocale).Must(l => JsonStringLocalizerFactory.Languages.Contains(l)).WithErrorCode("invalid_option");
        RuleFor(x => x.Branding).Must(b =>
        {
            try
            {
                return JsonDocument.Parse(b).RootElement.ValueKind == JsonValueKind.Object && b.Length <= 20_000;
            }
            catch (JsonException)
            {
                return false;
            }
        }).WithErrorCode(ValidationCodes.InvalidFormat);
    }
}

public sealed class UpdateTenantHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<UpdateTenantCommand, Unit>
{
    public async Task<Unit> Handle(UpdateTenantCommand c, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        var t = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == c.Id && x.DeletedAt == null, ct) ?? throw AppException.NotFound("tenant_not_found");
        var old = JsonSerializer.Serialize(new { t.Name, t.Plan, t.UserLimit, t.StorageQuotaBytes, t.OutboundDailyLimit, t.DefaultLocale, t.DefaultTimeZone, t.SuspendTemporary });
        (t.Name, t.Plan, t.UserLimit, t.StorageQuotaBytes, t.OutboundDailyLimit) = (c.Name.Trim(), c.Plan.Trim(), c.UserLimit, c.StorageQuotaBytes, c.OutboundDailyLimit);
        (t.DefaultLocale, t.DefaultTimeZone, t.SuspendTemporary, t.Branding) = (c.DefaultLocale, c.DefaultTimeZone, c.SuspendTemporary, c.Branding);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("tenant.update", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "tenant", t.Id.ToString(), old,
            JsonSerializer.Serialize(c)), ct);
        return Unit.Value;
    }
}

/// <summary>
/// A platform administrator asks to sign in as a user to help (PROMPT 7.1). The request is audited, the tenant's
/// administrators are told by mail, and a one-time link to BlueID is returned; the support session lasts one hour.
/// </summary>
public sealed record StartSupportSignInCommand(Guid TenantId, Guid UserId, string Reason) : IRequest<string>;

public sealed class StartSupportSignInValidator : AbstractValidator<StartSupportSignInCommand>
{
    public StartSupportSignInValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).WithErrorCode("support_reason_required");
}

public sealed class StartSupportSignInHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit, SystemMail mail, BmSettings settings)
    : IRequestHandler<StartSupportSignInCommand, string>
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task<string> Handle(StartSupportSignInCommand c, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == c.UserId && u.TenantId == c.TenantId && u.DeletedAt == null && u.Status == UserStatus.Active, ct)
            ?? throw AppException.NotFound("user_not_found");
        if (user.IsPlatformAdmin)
        {
            throw AppException.Forbidden("support_not_for_admins");
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.SupportTickets.Add(new SupportTicket
        {
            TenantId = c.TenantId, TargetUserId = user.Id, ActorUserId = me.UserId, TokenHash = Hash(token), Reason = c.Reason.Trim(),
            ExpiresAt = DateTimeOffset.UtcNow + TicketLifetime,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("support.signin.start", AuditResult.Success, c.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", user.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { target = user.PrimaryEmail, c.Reason })), ct);

        var admins = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == c.TenantId && u.IsTenantAdmin && u.DeletedAt == null && u.Status == UserStatus.Active).ToListAsync(ct);
        foreach (var a in admins)
        {
            var culture = CultureInfo.GetCultureInfo(a.Locale ?? "en");
            var args = new Dictionary<string, object?> { ["actor"] = me.Email, ["user"] = user.PrimaryEmail, ["reason"] = c.Reason.Trim() };
            var bytes = new MessageBuilder
            {
                From = new MailboxAddress("BlueMail", "postmaster@" + settings.MailHostname),
                To = [new MailboxAddress(a.DisplayName, a.PrimaryEmail)],
                Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("support.notice.subject", culture), culture, args),
                MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
                Date = DateTimeOffset.UtcNow,
                TextBody = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("support.notice.body", culture), culture, args),
                ExtraHeaders = [("Auto-Submitted", "auto-generated")],
            }.ToBytes();
            await mail.SendAsync(c.TenantId, null, "", a.PrimaryEmail, bytes, notification: true, a.Locale, null, ct);
        }

        return settings.IdentityUrl + "/account/support?ticket=" + token;
    }
}
