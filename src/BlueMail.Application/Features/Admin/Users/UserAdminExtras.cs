using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Contacts;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using DomainEmail = BlueMail.Domain.Common.EmailAddress;

namespace BlueMail.Application.Features.Admin.Users;

public sealed record LoginDto(string Protocol, string Result, string? Reason, string? Ip, DateTimeOffset At);

public sealed record UserDetailDto(Guid Id, string Email, string DisplayName, string Status, bool IsTenantAdmin, bool TwoFactorEnabled, long QuotaBytes, long UsedBytes,
    Guid? OrgUnitId, bool ImapEnabled, IReadOnlyList<string> Aliases, IReadOnlyList<LoginDto> RecentLogins, DateTimeOffset? LastLoginAt);

public sealed record GetUserQuery(Guid Id) : IRequest<UserDetailDto>;

public sealed class GetUserHandler(BlueMailDbContext db) : IRequestHandler<GetUserQuery, UserDetailDto>
{
    public async Task<UserDetailDto> Handle(GetUserQuery q, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw AppException.NotFound("user_not_found");
        var aliases = await db.UserAliases.AsNoTracking().Where(a => a.UserId == u.Id).OrderBy(a => a.Email).Select(a => a.Email).ToListAsync(ct);
        var mailbox = await db.Mailboxes.AsNoTracking().Where(m => m.OwnerId == u.Id).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        var used = mailbox is null ? 0 : await db.Messages.AsNoTracking().Where(m => m.MailboxId == mailbox && m.ExpungedAt == null && m.DeletedAt == null).SumAsync(m => (long?)m.Size, ct) ?? 0;
        // Recent sign-ins by protocol (PROMPT 7.3): web now, IMAP/SMTP from Phase 11 on, all in the same table.
        var logins = await db.LoginEvents.AsNoTracking().Where(e => e.UserId == u.Id).OrderByDescending(e => e.OccurredAt).Take(20)
            .Select(e => new LoginDto(e.Protocol.ToString().ToUpper(), e.Result.ToString().ToUpper(), e.Reason, e.Ip, e.OccurredAt)).ToListAsync(ct);
        return new UserDetailDto(u.Id, u.PrimaryEmail, u.DisplayName, EnumNames.ToUpperSnake(u.Status.ToString()), u.IsTenantAdmin, u.TwoFactorEnabled, u.QuotaBytes, used,
            u.OrgUnitId, u.ImapEnabled, aliases, logins, u.LastLoginAt);
    }
}

public sealed record UpdateUserCommand(Guid Id, string DisplayName, bool IsTenantAdmin, long QuotaBytes, Guid? OrgUnitId, bool ImapEnabled) : IRequest<Unit>;

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.QuotaBytes).GreaterThan(0).WithErrorCode(ValidationCodes.InvalidFormat);
    }
}

public sealed class UpdateUserHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit, ISessionValidator sessions) : IRequestHandler<UpdateUserCommand, Unit>
{
    public async Task<Unit> Handle(UpdateUserCommand c, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw AppException.NotFound("user_not_found");
        if (c.Id == me.UserId && !c.IsTenantAdmin && u.IsTenantAdmin)
        {
            throw AppException.BadRequest("cannot_change_own_status"); // an admin cannot lock themselves out
        }

        if (c.OrgUnitId is { } ou && !await db.OrgUnits.AnyAsync(o => o.Id == ou, ct))
        {
            throw AppException.NotFound("org_unit_not_found");
        }

        var old = JsonSerializer.Serialize(new { u.DisplayName, u.IsTenantAdmin, u.QuotaBytes, u.OrgUnitId, u.ImapEnabled });
        u.DisplayName = c.DisplayName.Trim();
        u.IsTenantAdmin = c.IsTenantAdmin;
        u.QuotaBytes = c.QuotaBytes;
        u.OrgUnitId = c.OrgUnitId;
        u.ImapEnabled = c.ImapEnabled;
        await db.SaveChangesAsync(ct);
        await sessions.InvalidateUserAsync(u.Id); // admin rights live in the cached session state
        await audit.WriteAsync(new AuditEntry("user.update", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", u.Id.ToString(), old,
            JsonSerializer.Serialize(c)), ct);
        return Unit.Value;
    }
}

/// <summary>Resets the password to a generated one shown once; the user must change it at the next sign-in; open sessions end.</summary>
public sealed record ResetPasswordCommand(Guid Id) : IRequest<string>;

public sealed class ResetPasswordHandler(BlueMailDbContext db, UserManager<User> users, ICurrentUser me, AuditWriter audit) : IRequestHandler<ResetPasswordCommand, string>
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewPassword() => new(Enumerable.Range(0, 16).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    public async Task<string> Handle(ResetPasswordCommand c, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw AppException.NotFound("user_not_found");
        var password = NewPassword();
        u.PasswordHash = users.PasswordHasher.HashPassword(u, password);
        u.MustChangePassword = true;
        await users.UpdateSecurityStampAsync(u); // tokens with the old stamp stop working
        await db.Sessions.Where(s => s.UserId == u.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("user.reset_password", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", u.Id.ToString()), ct);
        return password;
    }
}

public sealed record AddAliasCommand(Guid UserId, string Email) : IRequest<Unit>;

public sealed class AddAliasHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<AddAliasCommand, Unit>
{
    public async Task<Unit> Handle(AddAliasCommand c, CancellationToken ct)
    {
        _ = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.UserId, ct) ?? throw AppException.NotFound("user_not_found");
        var email = await AddressRules.FreeOwnedAddressAsync(db, me.TenantId, c.Email, ct);
        db.UserAliases.Add(new UserAlias { TenantId = me.TenantId, UserId = c.UserId, Email = email });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("email_taken");
        }

        await audit.WriteAsync(new AuditEntry("alias.add", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", c.UserId.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { email })), ct);
        return Unit.Value;
    }
}

public sealed record RemoveAliasCommand(Guid UserId, string Email) : IRequest<Unit>;

public sealed class RemoveAliasHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<RemoveAliasCommand, Unit>
{
    public async Task<Unit> Handle(RemoveAliasCommand c, CancellationToken ct)
    {
        var e = c.Email.Trim().ToLowerInvariant();
        if (await db.UserAliases.Where(a => a.UserId == c.UserId && a.Email == e).ExecuteUpdateAsync(a => a.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct) == 0)
        {
            throw AppException.NotFound("not_found");
        }

        await audit.WriteAsync(new AuditEntry("alias.remove", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", c.UserId.ToString(),
            JsonSerializer.Serialize(new { email = e })), ct);
        return Unit.Value;
    }
}

/// <summary>Address checks shared by users, aliases, groups and shared mailboxes: on a domain of the tenant and free everywhere.</summary>
public static class AddressRules
{
    public static async Task<string> FreeOwnedAddressAsync(BlueMailDbContext db, Guid tenant, string raw, CancellationToken ct)
    {
        if (!DomainEmail.TryParse(raw.Trim(), out var parsed))
        {
            throw new ValidationFailedException([new FieldError("email", ValidationCodes.InvalidEmail)]);
        }

        var email = parsed.Value;
        if (!await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant && d.Domain == email.Domain && d.DeletedAt == null, ct))
        {
            throw AppException.BadRequest("domain_not_in_tenant", new Dictionary<string, object?> { ["domain"] = email.DisplayDomain });
        }

        var taken = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.PrimaryEmail == email.Value && u.DeletedAt == null, ct)
                    || await db.UserAliases.IgnoreQueryFilters().AnyAsync(a => a.Email == email.Value && a.DeletedAt == null, ct)
                    || await db.Groups.IgnoreQueryFilters().AnyAsync(g => g.Email == email.Value && g.DeletedAt == null, ct)
                    || await db.SharedMailboxes.IgnoreQueryFilters().AnyAsync(s => s.Email == email.Value && s.DeletedAt == null, ct);
        return taken ? throw AppException.Conflict("email_taken") : email.Value;
    }
}

/// <summary>New primary address; the old one stays as an alias so nothing sent to it is lost (PROMPT 7.3).</summary>
public sealed record ChangePrimaryAddressCommand(Guid UserId, string NewEmail) : IRequest<Unit>;

public sealed class ChangePrimaryAddressHandler(BlueMailDbContext db, UserManager<User> users, ICurrentUser me, AuditWriter audit, ISessionValidator sessions)
    : IRequestHandler<ChangePrimaryAddressCommand, Unit>
{
    public async Task<Unit> Handle(ChangePrimaryAddressCommand c, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == c.UserId, ct) ?? throw AppException.NotFound("user_not_found");
        var email = await AddressRules.FreeOwnedAddressAsync(db, me.TenantId, c.NewEmail, ct);
        var old = u.PrimaryEmail;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.UserAliases.Add(new UserAlias { TenantId = u.TenantId, UserId = u.Id, Email = old });
        u.PrimaryEmail = email;
        u.Email = email;
        u.UserName = email;
        u.NormalizedEmail = users.NormalizeEmail(email);
        u.NormalizedUserName = users.NormalizeName(email);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await sessions.InvalidateUserAsync(u.Id);
        await audit.WriteAsync(new AuditEntry("user.change_address", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", u.Id.ToString(),
            JsonSerializer.Serialize(new { email = old }), JsonSerializer.Serialize(new { email })), ct);
        return Unit.Value;
    }
}

/// <summary>
/// Deletes a user (soft): sessions end, sign-in stops, the address is freed only by an administrator later.
/// Optionally first copies all their mail to another user under a label "From &lt;address&gt;" (PROMPT 7.3).
/// </summary>
public sealed record DeleteUserCommand(Guid Id, Guid? TransferToUserId) : IRequest<int>;

public sealed class DeleteUserHandler(BlueMailDbContext db, UserManager<User> users, PgMailStore store, ICurrentUser me, AuditWriter audit, ISessionValidator sessions)
    : IRequestHandler<DeleteUserCommand, int>
{
    public const int MaxTransfer = 100_000;

    public async Task<int> Handle(DeleteUserCommand c, CancellationToken ct)
    {
        if (c.Id == me.UserId)
        {
            throw AppException.BadRequest("cannot_change_own_status");
        }

        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw AppException.NotFound("user_not_found");
        var copied = 0;
        if (c.TransferToUserId is { } to)
        {
            var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == to && x.Status == UserStatus.Active, ct) ?? throw AppException.NotFound("user_not_found");
            var source = await store.OpenAsync(u.TenantId, u.Id, null, ct);
            var dest = await store.OpenAsync(target.TenantId, target.Id, null, ct);
            var label = await store.CreateLabelAsync(dest, ("Từ " + u.PrimaryEmail).Replace("/", "-", StringComparison.Ordinal), null, null, null, ct);
            var ids = await db.Messages.AsNoTracking().Where(m => m.MailboxId == source.MailboxId && m.ExpungedAt == null && m.DeletedAt == null && !m.Draft)
                .OrderBy(m => m.ReceivedAt).Select(m => new { m.Id, m.ReceivedAt, m.Seen }).Take(MaxTransfer).ToListAsync(ct);
            foreach (var m in ids)
            {
                using var raw = new MemoryStream();
                if (!await store.CopyRawAsync(source, m.Id, raw, ct))
                {
                    continue;
                }

                raw.Position = 0;
                var facts = MessageFacts.Extract(MimeMessage.Parse(raw), raw);
                raw.Position = 0;
                await store.DeliverAsync(new DeliveryRequest(dest.TenantId, raw, facts, [new MailboxDelivery(dest.MailboxId, [], m.Seen, ExtraLabelIds: [label])], m.ReceivedAt), ct);
                copied++;
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        u.Status = UserStatus.Deleted;
        u.DeletedAt = DateTimeOffset.UtcNow;
        await users.UpdateSecurityStampAsync(u);
        await db.Sessions.Where(s => s.UserId == u.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await sessions.InvalidateUserAsync(u.Id);
        await audit.WriteAsync(new AuditEntry("user.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", u.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { email = u.PrimaryEmail, transferredTo = c.TransferToUserId, copied })), ct);
        return copied;
    }
}

// ---------- CSV import ----------

public sealed record ImportUserRow(string Email, string DisplayName, string? Password, bool IsTenantAdmin, long? QuotaGb, string? OrgUnit);

public sealed record ImportRowError(int Row, string Field, string Code);

public sealed record GeneratedPassword(string Email, string Password);

public sealed record ImportUsersResult(IReadOnlyList<ImportUserRow> Rows, IReadOnlyList<ImportRowError> Errors, int Created, IReadOnlyList<GeneratedPassword> Passwords);

/// <summary>Reads a CSV: email, display name, password (optional), admin (yes/no), quota GB, org unit; the header names are free.</summary>
public sealed record ParseUsersCsvCommand(string Text) : IRequest<IReadOnlyList<ImportUserRow>>;

public sealed class ParseUsersCsvHandler : IRequestHandler<ParseUsersCsvCommand, IReadOnlyList<ImportUserRow>>
{
    public Task<IReadOnlyList<ImportUserRow>> Handle(ParseUsersCsvCommand c, CancellationToken ct)
    {
        var rows = ContactCsv.Parse(c.Text.TrimStart('﻿'));
        if (rows.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ImportUserRow>>([]);
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var (iEmail, iName, iPass, iAdmin, iQuota, iUnit) = (Col("email", "e-mail", "địa chỉ"), Col("name", "display name", "displayname", "tên", "họ tên"),
            Col("password", "mật khẩu"), Col("admin", "is admin", "quản trị"), Col("quota", "quota gb", "dung lượng"), Col("org unit", "orgunit", "đơn vị"));
        var hasHeader = iEmail >= 0;
        if (!hasHeader)
        {
            (iEmail, iName, iPass, iAdmin, iQuota, iUnit) = (0, 1, 2, 3, 4, 5);
        }

        string? At(IReadOnlyList<string> r, int i) => i >= 0 && i < r.Count && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
        var result = rows.Skip(hasHeader ? 1 : 0).Take(5000).Select(r => new ImportUserRow(At(r, iEmail) ?? "", At(r, iName) ?? "", At(r, iPass),
            At(r, iAdmin)?.ToLowerInvariant() is "yes" or "true" or "1" or "x" or "có", long.TryParse(At(r, iQuota), out var q) ? q : null, At(r, iUnit))).ToList();
        return Task.FromResult<IReadOnlyList<ImportUserRow>>(result);
    }
}

/// <summary>
/// Validates (and with <see cref="Commit"/> creates) users from rows the admin may have corrected in the table.
/// Nothing is created while any row has an error: the admin fixes the table and sends it again.
/// </summary>
public sealed record ImportUsersCommand(IReadOnlyList<ImportUserRow> Rows, bool Commit) : IRequest<ImportUsersResult>;

public sealed class ImportUsersHandler(BlueMailDbContext db, ICurrentUser me, UserProvisioning provisioning, Policies.PolicyService policies, AuditWriter audit)
    : IRequestHandler<ImportUsersCommand, ImportUsersResult>
{
    public const int MaxRows = 5000;

    public async Task<ImportUsersResult> Handle(ImportUsersCommand c, CancellationToken ct)
    {
        if (c.Rows.Count is 0 or > MaxRows)
        {
            throw AppException.BadRequest("import_size");
        }

        var errors = new List<ImportRowError>();
        var minPassword = await policies.IntAsync(me.TenantId, null, Policies.PolicyKeys.PasswordMinLength, ct);
        var domains = await db.Domains.AsNoTracking().Select(d => d.Domain).ToListAsync(ct);
        var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Name.ToLower(), o => o.Id, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emails = c.Rows.Select(r => r.Email.Trim().ToLowerInvariant()).ToList();
        var existing = (await db.Users.IgnoreQueryFilters().Where(u => emails.Contains(u.PrimaryEmail) && u.DeletedAt == null).Select(u => u.PrimaryEmail).ToListAsync(ct))
            .Concat(await db.UserAliases.IgnoreQueryFilters().Where(a => emails.Contains(a.Email) && a.DeletedAt == null).Select(a => a.Email).ToListAsync(ct))
            .Concat(await db.Groups.IgnoreQueryFilters().Where(g => emails.Contains(g.Email) && g.DeletedAt == null).Select(g => g.Email).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < c.Rows.Count; i++)
        {
            var r = c.Rows[i];
            var n = i + 1;
            if (!DomainEmail.TryParse(r.Email.Trim(), out var parsed))
            {
                errors.Add(new ImportRowError(n, "email", ValidationCodes.InvalidEmail));
            }
            else if (!domains.Contains(parsed.Value.Domain))
            {
                errors.Add(new ImportRowError(n, "email", "domain_not_in_tenant"));
            }
            else if (!seen.Add(parsed.Value.Value))
            {
                errors.Add(new ImportRowError(n, "email", "duplicate_in_file"));
            }
            else if (existing.Contains(parsed.Value.Value))
            {
                errors.Add(new ImportRowError(n, "email", "email_taken"));
            }

            if (string.IsNullOrWhiteSpace(r.DisplayName) || r.DisplayName.Length > 200)
            {
                errors.Add(new ImportRowError(n, "displayName", ValidationCodes.Required));
            }

            if (r.Password is { } p && p.Length < minPassword)
            {
                errors.Add(new ImportRowError(n, "password", ValidationCodes.PasswordTooWeak));
            }

            if (r.QuotaGb is { } q && q is <= 0 or > 10_000)
            {
                errors.Add(new ImportRowError(n, "quotaGb", ValidationCodes.InvalidFormat));
            }

            if (r.OrgUnit is { } ou && !units.ContainsKey(ou.ToLowerInvariant()))
            {
                errors.Add(new ImportRowError(n, "orgUnit", "org_unit_not_found"));
            }
        }

        if (!c.Commit || errors.Count > 0)
        {
            return new ImportUsersResult(c.Rows, errors, 0, []);
        }

        var passwords = new List<GeneratedPassword>();
        foreach (var r in c.Rows)
        {
            var password = r.Password ?? ResetPasswordHandler.NewPassword();
            var u = await provisioning.CreateAsync(new NewUser(me.TenantId, r.Email.Trim(), r.DisplayName.Trim(), password, r.IsTenantAdmin,
                QuotaBytes: r.QuotaGb is { } gb ? gb * 1024 * 1024 * 1024 : null, MustChangePassword: true), ct);
            if (r.OrgUnit is { } unit)
            {
                await db.Users.Where(x => x.Id == u.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.OrgUnitId, units[unit.ToLowerInvariant()]), ct);
            }

            if (r.Password is null)
            {
                passwords.Add(new GeneratedPassword(u.PrimaryEmail, password)); // generated: shown once to the admin
            }
        }

        await audit.WriteAsync(new AuditEntry("user.import", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", null,
            NewValueJson: JsonSerializer.Serialize(new { count = c.Rows.Count })), ct);
        return new ImportUsersResult(c.Rows, [], c.Rows.Count, passwords);
    }
}
