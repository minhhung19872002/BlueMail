using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MailboxAddress = BlueMail.Mime.MailboxAddress;

namespace BlueMail.Application.Features.Rules;

// ---------- filters ----------

public sealed record FilterDto(Guid Id, string Criteria, FilterActions Actions, int SortOrder, bool IsEnabled);

public sealed record ListFiltersQuery : IRequest<IReadOnlyList<FilterDto>>;

public sealed class ListFiltersHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<ListFiltersQuery, IReadOnlyList<FilterDto>>
{
    public async Task<IReadOnlyList<FilterDto>> Handle(ListFiltersQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var rows = await db.Filters.AsNoTracking().Where(f => f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt).ToListAsync(ct);
        return rows.Select(f => new FilterDto(f.Id, f.Criteria, FilterActions.Parse(f.Actions), f.SortOrder, f.IsEnabled)).ToList();
    }
}

public sealed record SaveFilterCommand(Guid? Id, string Criteria, FilterActions Actions, bool IsEnabled = true) : IRequest<Guid>;

public sealed class SaveFilterValidator : AbstractValidator<SaveFilterCommand>
{
    public SaveFilterValidator()
    {
        RuleFor(x => x.Criteria).NotEmpty().WithErrorCode("filter_no_criteria").MaximumLength(SearchParser.MaxLength).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Actions).NotNull().Must(a => !a.IsEmpty).WithErrorCode("filter_no_action");
        RuleFor(x => x.Actions.Important).Must(v => v is null or "ALWAYS" or "NEVER").WithErrorCode("invalid_option");
        RuleFor(x => x.Actions.Category).Must(v => v is null or "PRIMARY" or "SOCIAL" or "PROMOTIONS" or "UPDATES" or "FORUMS").WithErrorCode("invalid_option");
        RuleFor(x => x.Actions.LabelIds.Count).LessThanOrEqualTo(20);
    }
}

/// <summary>Shared checks of filter actions: labels of the caller's mailbox, forwarding only to verified addresses.</summary>
public sealed class FilterRules(BlueMailDbContext db)
{
    public const int MaxFilters = 1000;

    public async Task CheckAsync(MailboxAccess access, FilterActions a, CancellationToken ct)
    {
        if (a.LabelIds.Count > 0)
        {
            var own = await db.Labels.AsNoTracking().CountAsync(l => a.LabelIds.Contains(l.Id) && l.MailboxId == access.MailboxId && l.SystemKey == null && l.DeletedAt == null, ct);
            if (own != a.LabelIds.Distinct().Count())
            {
                throw AppException.BadRequest("label_not_found");
            }
        }

        if (a.ForwardTo is { } to && !await db.ForwardingAddresses.AnyAsync(f => f.UserId == access.ActorUserId && f.TargetEmail == to.ToLowerInvariant()
                                                                                   && f.VerifiedAt != null && f.DeletedAt == null, ct))
        {
            throw AppException.BadRequest("forward_not_verified");
        }
    }
}

public sealed class SaveFilterHandler(MailboxOpener opener, BlueMailDbContext db, SearchContexts contexts, FilterRules rules) : IRequestHandler<SaveFilterCommand, Guid>
{
    public async Task<Guid> Handle(SaveFilterCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var owner = await contexts.ForMailboxAsync(access.TenantId, access.MailboxId, access.ActorUserId, ct);
        if (SearchContexts.Parse(c.Criteria, owner) is null)
        {
            throw AppException.BadRequest("filter_no_criteria");
        }

        var actions = c.Actions with { ForwardTo = c.Actions.ForwardTo?.Trim().ToLowerInvariant() is { Length: > 0 } f ? f : null };
        await rules.CheckAsync(access, actions, ct);
        MailFilter? row;
        if (c.Id is { } id)
        {
            row = await db.Filters.FirstOrDefaultAsync(f => f.Id == id && f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId, ct)
                  ?? throw AppException.NotFound("filter_not_found");
        }
        else
        {
            if (await db.Filters.CountAsync(f => f.MailboxId == access.MailboxId, ct) >= FilterRules.MaxFilters)
            {
                throw AppException.BadRequest("too_many_filters");
            }

            var next = await db.Filters.Where(f => f.MailboxId == access.MailboxId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
            row = new MailFilter { TenantId = access.TenantId, UserId = access.ActorUserId, MailboxId = access.MailboxId, SortOrder = next + 1 };
            db.Filters.Add(row);
        }

        row.Criteria = c.Criteria.Trim();
        row.Actions = actions.ToJson();
        row.IsEnabled = c.IsEnabled;
        await db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public sealed record DeleteFilterCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteFilterHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<DeleteFilterCommand, Unit>
{
    public async Task<Unit> Handle(DeleteFilterCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var n = await db.Filters.Where(f => f.Id == c.Id && f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct);
        return n == 0 ? throw AppException.NotFound("filter_not_found") : Unit.Value;
    }
}

public sealed record ReorderFiltersCommand(IReadOnlyList<Guid> Ids) : IRequest<Unit>;

public sealed class ReorderFiltersHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<ReorderFiltersCommand, Unit>
{
    public async Task<Unit> Handle(ReorderFiltersCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var rows = await db.Filters.Where(f => f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId).ToListAsync(ct);
        var order = c.Ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        foreach (var f in rows)
        {
            f.SortOrder = order.TryGetValue(f.Id, out var i) ? i + 1 : rows.Count + f.SortOrder;
        }

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Counts what a query matches now (for "Also apply to N matching conversations" and the filter preview).</summary>
public sealed record CountMatchesQuery(string Criteria) : IRequest<int>;

public sealed class CountMatchesHandler(MailboxOpener opener, PgMailStore store, SearchContexts contexts) : IRequestHandler<CountMatchesQuery, int>
{
    public const int Cap = 5000;

    public async Task<int> Handle(CountMatchesQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var owner = await contexts.ForMailboxAsync(access.TenantId, access.MailboxId, access.ActorUserId, ct);
        var node = SearchContexts.Parse(q.Criteria, owner) ?? throw AppException.BadRequest("filter_no_criteria");
        return (await store.SearchMessagesAsync(access, node, owner.Addresses, null, Cap, ct)).Count;
    }
}

public sealed record FilterJobDto(Guid Id, string Status, int Total, int Done);

public sealed record ApplyFilterCommand(Guid FilterId) : IRequest<FilterJobDto>;

public sealed class ApplyFilterHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<ApplyFilterCommand, FilterJobDto>
{
    public async Task<FilterJobDto> Handle(ApplyFilterCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        _ = await db.Filters.AsNoTracking().FirstOrDefaultAsync(f => f.Id == c.FilterId && f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId, ct)
            ?? throw AppException.NotFound("filter_not_found");
        var job = new FilterJob
        {
            Id = Guid.NewGuid(), TenantId = access.TenantId, UserId = access.ActorUserId, MailboxId = access.MailboxId, FilterId = c.FilterId,
            Status = FilterJobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.FilterJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return new FilterJobDto(job.Id, "QUEUED", 0, 0);
    }
}

public sealed record GetFilterJobQuery(Guid Id) : IRequest<FilterJobDto>;

public sealed class GetFilterJobHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetFilterJobQuery, FilterJobDto>
{
    public async Task<FilterJobDto> Handle(GetFilterJobQuery q, CancellationToken ct)
    {
        var j = await db.FilterJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id && x.UserId == me.UserId, ct) ?? throw AppException.NotFound("job_not_found");
        return new FilterJobDto(j.Id, EnumNames.ToUpperSnake(j.Status.ToString()), j.Total, j.Done);
    }
}

public sealed record ExportFiltersQuery : IRequest<string>;

public sealed class ExportFiltersHandler(MailboxOpener opener, BlueMailDbContext db) : IRequestHandler<ExportFiltersQuery, string>
{
    public async Task<string> Handle(ExportFiltersQuery q, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == access.ActorUserId, ct);
        var labels = await db.Labels.AsNoTracking().Where(l => l.MailboxId == access.MailboxId && l.DeletedAt == null).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var rows = await db.Filters.AsNoTracking().Where(f => f.MailboxId == access.MailboxId && f.UserId == access.ActorUserId).OrderBy(f => f.SortOrder).ToListAsync(ct);
        var portable = rows.Select(f =>
        {
            var a = FilterActions.Parse(f.Actions);
            return new PortableFilter(f.Criteria, a with { LabelIds = [] }, a.LabelIds.Where(labels.ContainsKey).Select(id => labels[id]).ToList());
        });
        return GmailFilters.Export(portable, user.DisplayName, user.PrimaryEmail, DateTimeOffset.UtcNow);
    }
}

public sealed record ImportResultDto(int Imported, IReadOnlyList<ImportProblemDto> Problems);

public sealed record ImportProblemDto(int Entry, string Code, string? Detail);

public sealed record ImportFiltersCommand(Stream Xml) : IRequest<ImportResultDto>;

/// <summary>Gmail import: labels are created when missing; forwards to unverified addresses are dropped and reported.</summary>
public sealed class ImportFiltersHandler(MailboxOpener opener, BlueMailDbContext db, PgMailStore store, SearchContexts contexts) : IRequestHandler<ImportFiltersCommand, ImportResultDto>
{
    public async Task<ImportResultDto> Handle(ImportFiltersCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(null, ct);
        IReadOnlyList<PortableFilter> filters;
        List<ImportProblemDto> problems;
        try
        {
            var (f, p) = GmailFilters.Import(c.Xml);
            filters = f;
            problems = p.Select(x => new ImportProblemDto(x.Entry, x.Code, x.Detail)).ToList();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            throw AppException.BadRequest("not_gmail_filters");
        }

        var owner = await contexts.ForMailboxAsync(access.TenantId, access.MailboxId, access.ActorUserId, ct);
        var verified = await db.ForwardingAddresses.AsNoTracking().Where(x => x.UserId == access.ActorUserId && x.VerifiedAt != null).Select(x => x.TargetEmail).ToListAsync(ct);
        var next = await db.Filters.Where(f => f.MailboxId == access.MailboxId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var imported = 0;
        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            try
            {
                SearchContexts.Parse(f.Criteria, owner);
            }
            catch (AppException e)
            {
                problems.Add(new ImportProblemDto(i + 1, e.Code, f.Criteria));
                continue;
            }

            var actions = f.Actions;
            if (actions.ForwardTo is { } fw && !verified.Contains(fw))
            {
                problems.Add(new ImportProblemDto(i + 1, "forward_not_verified", fw));
                actions = actions with { ForwardTo = null };
            }

            var labelIds = new List<Guid>();
            foreach (var name in f.LabelNames)
            {
                labelIds.Add(await LabelIdAsync(access, name, ct));
            }

            actions = actions with { LabelIds = labelIds };
            if (actions.IsEmpty)
            {
                continue;
            }

            db.Filters.Add(new MailFilter
            {
                TenantId = access.TenantId, UserId = access.ActorUserId, MailboxId = access.MailboxId, Criteria = f.Criteria, Actions = actions.ToJson(),
                SortOrder = ++next,
            });
            imported++;
        }

        await db.SaveChangesAsync(ct);
        return new ImportResultDto(imported, problems);
    }

    private async Task<Guid> LabelIdAsync(MailboxAccess access, string name, CancellationToken ct)
    {
        var existing = await db.Labels.AsNoTracking()
            .Where(l => l.MailboxId == access.MailboxId && l.SystemKey == null && l.DeletedAt == null && l.Name.ToLower() == name.ToLower()).Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
        return existing ?? await store.CreateLabelAsync(access, name.Trim(), null, null, null, ct);
    }
}

// ---------- blocked senders ----------

public sealed record BlockedSenderDto(Guid Id, string AddressOrDomain, DateTimeOffset CreatedAt);

public sealed record ListBlockedQuery : IRequest<IReadOnlyList<BlockedSenderDto>>;

public sealed class ListBlockedHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListBlockedQuery, IReadOnlyList<BlockedSenderDto>>
{
    public async Task<IReadOnlyList<BlockedSenderDto>> Handle(ListBlockedQuery q, CancellationToken ct) =>
        await db.BlockedSenders.AsNoTracking().Where(b => b.UserId == me.UserId).OrderBy(b => b.AddressOrDomain)
            .Select(b => new BlockedSenderDto(b.Id, b.AddressOrDomain, b.CreatedAt)).ToListAsync(ct);
}

public sealed record BlockSenderCommand(string AddressOrDomain) : IRequest<Guid>;

public sealed class BlockSenderValidator : AbstractValidator<BlockSenderCommand>
{
    public BlockSenderValidator() =>
        RuleFor(x => x.AddressOrDomain).NotEmpty().MaximumLength(320).Must(BlockSenderHandler.IsValid).WithErrorCode(ValidationCodes.InvalidFormat);
}

public sealed class BlockSenderHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<BlockSenderCommand, Guid>
{
    public static string Normalize(string v) => v.Trim().ToLowerInvariant() is var s && s.StartsWith('@') ? s[1..] : v.Trim().ToLowerInvariant();

    public static bool IsValid(string v)
    {
        var s = Normalize(v);
        return s.Contains('@', StringComparison.Ordinal) ? MailAddress.TryCreate(s, out _) : s.Contains('.', StringComparison.Ordinal) && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');
    }

    public async Task<Guid> Handle(BlockSenderCommand c, CancellationToken ct)
    {
        var value = Normalize(c.AddressOrDomain);
        var existing = await db.BlockedSenders.FirstOrDefaultAsync(b => b.UserId == me.UserId && b.AddressOrDomain == value, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var row = new BlockedSender { TenantId = me.TenantId, UserId = me.UserId, AddressOrDomain = value };
        db.BlockedSenders.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public sealed record UnblockSenderCommand(Guid Id) : IRequest<Unit>;

public sealed class UnblockSenderHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<UnblockSenderCommand, Unit>
{
    public async Task<Unit> Handle(UnblockSenderCommand c, CancellationToken ct)
    {
        var n = await db.BlockedSenders.Where(b => b.Id == c.Id && b.UserId == me.UserId).ExecuteUpdateAsync(b => b.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct);
        return n == 0 ? throw AppException.NotFound("not_found") : Unit.Value;
    }
}

// ---------- vacation ----------

public sealed record VacationDto(bool Enabled, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string Subject, string Html, bool OnlyContacts, bool OnlyOrganization);

public sealed record GetVacationQuery : IRequest<VacationDto>;

public sealed class GetVacationHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetVacationQuery, VacationDto>
{
    public async Task<VacationDto> Handle(GetVacationQuery q, CancellationToken ct)
    {
        var v = await db.Vacations.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.UserId, ct);
        return v is null ? new VacationDto(false, null, null, "", "", false, false)
            : new VacationDto(v.Enabled, v.StartAt, v.EndAt, v.Subject, v.Html, v.OnlyContacts, v.OnlyOrganization);
    }
}

public sealed record SaveVacationCommand(bool Enabled, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string Subject, string Html, bool OnlyContacts, bool OnlyOrganization)
    : IRequest<Unit>;

public sealed class SaveVacationValidator : AbstractValidator<SaveVacationCommand>
{
    public SaveVacationValidator()
    {
        RuleFor(x => x.Subject).MaximumLength(500).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Html).MaximumLength(100_000).WithErrorCode(ValidationCodes.TooLong);
        RuleFor(x => x.Html).NotEmpty().When(x => x.Enabled).WithErrorCode("vacation_no_message");
        RuleFor(x => x.EndAt).GreaterThan(x => x.StartAt).When(x => x.StartAt is not null && x.EndAt is not null).WithErrorCode("date_range_reversed");
    }
}

public sealed class SaveVacationHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<SaveVacationCommand, Unit>
{
    public async Task<Unit> Handle(SaveVacationCommand c, CancellationToken ct)
    {
        var v = await db.Vacations.FirstOrDefaultAsync(x => x.UserId == me.UserId, ct);
        if (v is null)
        {
            v = new Vacation { UserId = me.UserId, TenantId = me.TenantId };
            db.Vacations.Add(v);
        }

        v.Enabled = c.Enabled;
        v.StartAt = c.StartAt;
        v.EndAt = c.EndAt;
        v.Subject = c.Subject.Trim();
        v.Html = MailHtml.Sanitize(c.Html, _ => null, u => u, showRemote: true).Html;
        v.OnlyContacts = c.OnlyContacts;
        v.OnlyOrganization = c.OnlyOrganization;
        v.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // A changed or restarted auto reply answers everyone again once (as Gmail).
        await db.VacationReplies.Where(r => r.UserId == me.UserId).ExecuteDeleteAsync(ct);
        return Unit.Value;
    }
}

// ---------- forwarding ----------

public sealed record ForwardingDto(Guid Id, string TargetEmail, bool Verified, bool IsActive, bool KeepCopy);

public sealed record ForwardingListDto(IReadOnlyList<ForwardingDto> Addresses, bool ExternalAllowed);

public sealed record ListForwardingQuery : IRequest<ForwardingListDto>;

public sealed class ListForwardingHandler(BlueMailDbContext db, ICurrentUser me, Admin.Policies.PolicyService policies) : IRequestHandler<ListForwardingQuery, ForwardingListDto>
{
    public async Task<ForwardingListDto> Handle(ListForwardingQuery q, CancellationToken ct)
    {
        var list = await db.ForwardingAddresses.AsNoTracking().Where(f => f.UserId == me.UserId).OrderBy(f => f.TargetEmail)
            .Select(f => new ForwardingDto(f.Id, f.TargetEmail, f.VerifiedAt != null, f.IsActive, f.KeepCopy)).ToListAsync(ct);
        var allowed = await policies.BoolAsync(me.TenantId, await policies.OrgUnitOfAsync(me.UserId, ct), Admin.Policies.PolicyKeys.AllowExternalForwarding, ct);
        return new ForwardingListDto(list, allowed);
    }
}

public sealed record AddForwardingCommand(string TargetEmail) : IRequest<Guid>;

public sealed class AddForwardingValidator : AbstractValidator<AddForwardingCommand>
{
    public AddForwardingValidator() =>
        RuleFor(x => x.TargetEmail).NotEmpty().MaximumLength(320).Must(e => MailAddress.TryCreate(e.Trim(), out _)).WithErrorCode(ValidationCodes.InvalidFormat);
}

/// <summary>Adds an address and mails it a verification code (valid 24 hours, 5 attempts). Nothing is forwarded before.</summary>
public sealed class AddForwardingHandler(BlueMailDbContext db, ICurrentUser me, SystemMail mail, BmSettings settings, AuditWriter audit,
    Admin.Policies.PolicyService policies) : IRequestHandler<AddForwardingCommand, Guid>
{
    public const int MaxAddresses = 20;

    public async Task<Guid> Handle(AddForwardingCommand c, CancellationToken ct)
    {
        var target = c.TargetEmail.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.UserId, ct);
        if (target == user.PrimaryEmail.ToLowerInvariant())
        {
            throw AppException.BadRequest("forward_to_self");
        }

        var domain = target[(target.LastIndexOf('@') + 1)..];
        var internalDomain = await db.Domains.AnyAsync(d => d.Domain == domain, ct);
        var allowed = await policies.BoolAsync(me.TenantId, await policies.OrgUnitOfAsync(me.UserId, ct), Admin.Policies.PolicyKeys.AllowExternalForwarding, ct);
        if (!internalDomain && !allowed)
        {
            throw AppException.Forbidden("external_forwarding_disabled");
        }

        var row = await db.ForwardingAddresses.FirstOrDefaultAsync(f => f.UserId == me.UserId && f.TargetEmail == target, ct);
        if (row is null)
        {
            if (await db.ForwardingAddresses.CountAsync(f => f.UserId == me.UserId, ct) >= MaxAddresses)
            {
                throw AppException.BadRequest("too_many_forwarding_addresses");
            }

            row = new ForwardingAddress { TenantId = me.TenantId, UserId = me.UserId, TargetEmail = target };
            db.ForwardingAddresses.Add(row);
        }
        else if (row.VerifiedAt is not null)
        {
            return row.Id;
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        row.VerificationCodeHash = CodeHash(row.Id == Guid.Empty ? (row.Id = Guid.NewGuid()) : row.Id, code);
        row.CodeExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        row.FailedAttempts = 0;
        await db.SaveChangesAsync(ct);

        var culture = CultureInfo.GetCultureInfo(user.Locale ?? "en");
        var args = new Dictionary<string, object?> { ["user"] = user.PrimaryEmail, ["code"] = code };
        var body = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("forwarding.verify.body", culture), culture, args);
        var userDomain = user.PrimaryEmail[(user.PrimaryEmail.LastIndexOf('@') + 1)..];
        var message = new MessageBuilder
        {
            From = new MailboxAddress("BlueMail", "no-reply@" + userDomain),
            To = [new MailboxAddress(null, target)],
            Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("forwarding.verify.subject", culture), culture, args),
            MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            Date = DateTimeOffset.UtcNow,
            TextBody = body,
            ExtraHeaders = [("Auto-Submitted", "auto-generated")],
        }.ToBytes();
        await mail.SendAsync(me.TenantId, me.UserId, "", target, await mail.SignAsync(me.TenantId, userDomain, message, ct), notification: true, user.Locale, null, ct);
        await audit.WriteAsync(new AuditEntry("forwarding.add", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "forwarding", target), ct);
        return row.Id;
    }

    public static byte[] CodeHash(Guid id, string code) => SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N") + ":" + code));
}

public sealed record VerifyForwardingCommand(Guid Id, string Code) : IRequest<Unit>;

public sealed class VerifyForwardingHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<VerifyForwardingCommand, Unit>
{
    public const int MaxAttempts = 5;

    public async Task<Unit> Handle(VerifyForwardingCommand c, CancellationToken ct)
    {
        var row = await db.ForwardingAddresses.FirstOrDefaultAsync(f => f.Id == c.Id && f.UserId == me.UserId, ct) ?? throw AppException.NotFound("not_found");
        if (row.VerifiedAt is not null)
        {
            return Unit.Value;
        }

        if (row.VerificationCodeHash is null || row.CodeExpiresAt < DateTimeOffset.UtcNow || row.FailedAttempts >= MaxAttempts)
        {
            throw AppException.BadRequest("code_expired");
        }

        if (!CryptographicOperations.FixedTimeEquals(row.VerificationCodeHash, AddForwardingHandler.CodeHash(row.Id, c.Code.Trim())))
        {
            row.FailedAttempts++;
            await db.SaveChangesAsync(ct);
            throw AppException.BadRequest("code_invalid");
        }

        row.VerifiedAt = DateTimeOffset.UtcNow;
        row.VerificationCodeHash = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("forwarding.verify", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "forwarding", row.TargetEmail), ct);
        return Unit.Value;
    }
}

/// <summary>Turns automatic forwarding on for one verified address (off for the others) or off entirely (Id null).</summary>
public sealed record SetActiveForwardingCommand(Guid? Id, bool KeepCopy) : IRequest<Unit>;

public sealed class SetActiveForwardingHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<SetActiveForwardingCommand, Unit>
{
    public async Task<Unit> Handle(SetActiveForwardingCommand c, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ForwardingAddresses.Where(f => f.UserId == me.UserId && f.IsActive).ExecuteUpdateAsync(f => f.SetProperty(x => x.IsActive, false), ct);
        string? target = null;
        if (c.Id is { } id)
        {
            var row = await db.ForwardingAddresses.FirstOrDefaultAsync(f => f.Id == id && f.UserId == me.UserId, ct) ?? throw AppException.NotFound("not_found");
            if (row.VerifiedAt is null)
            {
                throw AppException.BadRequest("forward_not_verified");
            }

            row.IsActive = true;
            row.KeepCopy = c.KeepCopy;
            target = row.TargetEmail;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        await audit.WriteAsync(new AuditEntry(target is null ? "forwarding.disable" : "forwarding.enable", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip,
            me.UserAgent, "forwarding", target), ct);
        return Unit.Value;
    }
}

public sealed record DeleteForwardingCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteForwardingHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<DeleteForwardingCommand, Unit>
{
    public async Task<Unit> Handle(DeleteForwardingCommand c, CancellationToken ct)
    {
        var n = await db.ForwardingAddresses.Where(f => f.Id == c.Id && f.UserId == me.UserId)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow).SetProperty(x => x.IsActive, false), ct);
        return n == 0 ? throw AppException.NotFound("not_found") : Unit.Value;
    }
}
