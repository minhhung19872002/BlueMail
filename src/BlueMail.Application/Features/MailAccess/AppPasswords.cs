using System.Security.Cryptography;
using BlueMail.Application.Common;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.MailAccess;

public sealed record AppPasswordDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The new password, shown once, and the settings a mail program needs.</summary>
public sealed record NewAppPasswordDto(Guid Id, string Password, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort);

public sealed record ListAppPasswordsQuery : IRequest<IReadOnlyList<AppPasswordDto>>;

public sealed class ListAppPasswordsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListAppPasswordsQuery, IReadOnlyList<AppPasswordDto>>
{
    public async Task<IReadOnlyList<AppPasswordDto>> Handle(ListAppPasswordsQuery q, CancellationToken ct) =>
        await db.AppPasswords.AsNoTracking().Where(a => a.UserId == me.UserId && a.RevokedAt == null && a.DeletedAt == null).OrderByDescending(a => a.CreatedAt)
            .Select(a => new AppPasswordDto(a.Id, a.Name, a.CreatedAt, a.LastUsedAt)).ToListAsync(ct);
}

public sealed record CreateAppPasswordCommand(string Name) : IRequest<NewAppPasswordDto>;

public sealed class CreateAppPasswordValidator : AbstractValidator<CreateAppPasswordCommand>
{
    public CreateAppPasswordValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}

public sealed class CreateAppPasswordHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit, Infrastructure.Configuration.BmSettings settings)
    : IRequestHandler<CreateAppPasswordCommand, NewAppPasswordDto>
{
    public const int MaxPerUser = 20;

    public async Task<NewAppPasswordDto> Handle(CreateAppPasswordCommand c, CancellationToken ct)
    {
        if (await db.AppPasswords.CountAsync(a => a.UserId == me.UserId && a.RevokedAt == null && a.DeletedAt == null, ct) >= MaxPerUser)
        {
            throw AppException.Conflict("app_password_limit");
        }

        // 16 letters (75 bits), shown in groups of four like Google's.
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        var letters = new string(Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        var shown = string.Join(' ', Enumerable.Range(0, 4).Select(i => letters.Substring(i * 4, 4)));
        var row = new AppPassword { TenantId = me.TenantId, UserId = me.UserId, Name = c.Name.Trim(), SecretHash = MailLoginService.HashAppPassword(letters) };
        db.AppPasswords.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("app_password.create", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "app_password", row.Id.ToString()), ct);
        var platform = settings.MailHostname.Contains('.', StringComparison.Ordinal) ? settings.MailHostname[(settings.MailHostname.IndexOf('.', StringComparison.Ordinal) + 1)..] : settings.MailHostname;
        return new NewAppPasswordDto(row.Id, shown, "imap." + platform, 993, "smtp." + platform, 465);
    }
}

public sealed record RevokeAppPasswordCommand(Guid Id) : IRequest<Unit>;

public sealed class RevokeAppPasswordHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit, ISessionValidator sessions) : IRequestHandler<RevokeAppPasswordCommand, Unit>
{
    public async Task<Unit> Handle(RevokeAppPasswordCommand c, CancellationToken ct)
    {
        var n = await db.AppPasswords.Where(a => a.Id == c.Id && a.UserId == me.UserId && a.RevokedAt == null)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        if (n == 0)
        {
            throw AppException.NotFound("app_password_not_found");
        }

        // Mail programs signed in with it are disconnected now (PROMPT 7.3), through the same channel as the web.
        await sessions.InvalidateUserAsync(me.UserId);
        await audit.WriteAsync(new AuditEntry("app_password.revoke", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "app_password", c.Id.ToString()), ct);
        return Unit.Value;
    }
}
