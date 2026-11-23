using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Compliance;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Compliance;

/// <summary>Shared pieces: parsing compliance queries in the tenant's time zone, mailboxes in a scope, hold recomputation.</summary>
public sealed class ComplianceScope(BlueMailDbContext db, PgMailStore store)
{
    /// <summary>Hard limit of messages a query hold may cover (each one is a row).</summary>
    public const int MaxQueryHold = 500_000;

    public async Task<SearchNode?> ParseAsync(Guid tenantId, string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var tz = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct);
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var z) ? z : TimeZoneInfo.Utc;
        return SearchContexts.Parse(query, new SearchOwner(tenantId, Guid.Empty, [], zone));
    }

    /// <summary>Mailboxes of a user, of the users of an org unit, or (null) of the whole tenant.</summary>
    public async Task<List<Guid>> MailboxesAsync(Guid tenantId, Guid? userId, Guid? orgUnitId, CancellationToken ct)
    {
        var q = db.Mailboxes.IgnoreQueryFilters().Where(m => m.TenantId == tenantId && m.DeletedAt == null);
        if (userId is { } u)
        {
            q = q.Where(m => m.OwnerId == u);
        }
        else if (orgUnitId is { } o)
        {
            q = q.Where(m => db.Users.IgnoreQueryFilters().Any(x => x.Id == m.OwnerId && x.OrgUnitId == o));
        }

        return await q.Select(m => m.Id).ToListAsync(ct);
    }

    /// <summary>Recomputes legal_hold_count for the mailboxes a hold touches (docs/00 D22: from the source, never added up).</summary>
    public async Task RecomputeAsync(LegalHold h, CancellationToken ct)
    {
        var mailboxes = h.Scope switch
        {
            HoldScope.User => await MailboxesAsync(h.TenantId, h.UserId, null, ct),
            HoldScope.OrgUnit => await MailboxesAsync(h.TenantId, null, h.OrgUnitId, ct),
            _ => await (from hm in db.HoldMessages.IgnoreQueryFilters()
                        join m in db.Messages.IgnoreQueryFilters() on hm.MessageId equals m.Id
                        where hm.HoldId == h.Id && m.TenantId == h.TenantId
                        select m.MailboxId).Distinct().ToListAsync(ct),
        };
        await store.RecomputeHoldCountsAsync(h.TenantId, mailboxes, ct);
    }
}

// ---- legal holds ----

public sealed record LegalHoldDto(Guid Id, string Name, string Scope, Guid? UserId, string? UserEmail, Guid? OrgUnitId, string? OrgUnitName, string? Query,
    string? Reason, int Matched, DateTimeOffset CreatedAt, DateTimeOffset? ReleasedAt);

public sealed record ListLegalHoldsQuery : IRequest<IReadOnlyList<LegalHoldDto>>;

public sealed class ListLegalHoldsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListLegalHoldsQuery, IReadOnlyList<LegalHoldDto>>
{
    public async Task<IReadOnlyList<LegalHoldDto>> Handle(ListLegalHoldsQuery q, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceView);
        return await db.LegalHolds.AsNoTracking().Where(h => h.DeletedAt == null).OrderByDescending(h => h.CreatedAt)
            .Select(h => new LegalHoldDto(h.Id, h.Name, h.Scope == HoldScope.User ? "USER" : h.Scope == HoldScope.OrgUnit ? "ORG_UNIT" : "QUERY", h.UserId,
                db.Users.Where(u => u.Id == h.UserId).Select(u => u.PrimaryEmail).FirstOrDefault(), h.OrgUnitId,
                db.OrgUnits.Where(o => o.Id == h.OrgUnitId).Select(o => o.Name).FirstOrDefault(), h.Query, h.Reason, h.Matched, h.CreatedAt, h.ReleasedAt))
            .ToListAsync(ct);
    }
}

public sealed record CreateLegalHoldCommand(string Name, string Scope, Guid? UserId, Guid? OrgUnitId, string? Query, string? Reason) : IRequest<Guid>;

public sealed class CreateLegalHoldValidator : AbstractValidator<CreateLegalHoldCommand>
{
    public CreateLegalHoldValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Scope).Must(s => s is "USER" or "ORG_UNIT" or "QUERY").WithErrorCode("invalid_option");
        RuleFor(x => x.UserId).NotNull().When(x => x.Scope == "USER");
        RuleFor(x => x.OrgUnitId).NotNull().When(x => x.Scope == "ORG_UNIT");
        RuleFor(x => x.Query).NotEmpty().MaximumLength(SearchParser.MaxLength).When(x => x.Scope == "QUERY");
        RuleFor(x => x.Reason).MaximumLength(2000);
    }
}

public sealed class CreateLegalHoldHandler(BlueMailDbContext db, ICurrentUser me, ComplianceScope scope, PgMailStore store, AuditWriter audit)
    : IRequestHandler<CreateLegalHoldCommand, Guid>
{
    public async Task<Guid> Handle(CreateLegalHoldCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceEdit);
        var hold = new LegalHold
        {
            Id = Guid.NewGuid(), TenantId = me.TenantId, Name = c.Name.Trim(), Reason = c.Reason?.Trim(),
            Scope = c.Scope switch { "USER" => HoldScope.User, "ORG_UNIT" => HoldScope.OrgUnit, _ => HoldScope.Query },
        };
        if (hold.Scope == HoldScope.User)
        {
            hold.UserId = await db.Users.Where(u => u.Id == c.UserId && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                          ?? throw AppException.BadRequest("user_not_found");
        }
        else if (hold.Scope == HoldScope.OrgUnit)
        {
            hold.OrgUnitId = await db.OrgUnits.Where(o => o.Id == c.OrgUnitId && o.DeletedAt == null).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct)
                             ?? throw AppException.BadRequest("org_unit_not_found");
        }

        var node = hold.Scope == HoldScope.Query ? await scope.ParseAsync(me.TenantId, c.Query, ct) ?? throw AppException.BadRequest("search_empty") : null;
        hold.Query = hold.Scope == HoldScope.Query ? c.Query!.Trim() : null;
        db.LegalHolds.Add(hold);
        await db.SaveChangesAsync(ct);

        if (node is not null)
        {
            // A query hold covers what matches now (deleted-but-kept mail included), snapshot into hold_messages.
            (DateTimeOffset, Guid)? after = null;
            var matched = 0;
            while (matched < ComplianceScope.MaxQueryHold)
            {
                var page = await store.ComplianceSearchAsync(me.TenantId, null, node, null, includeHidden: true, after, 5000, ct);
                if (page.Count == 0)
                {
                    break;
                }

                db.HoldMessages.AddRange(page.Select(h => new HoldMessage { TenantId = me.TenantId, HoldId = hold.Id, MessageId = h.MessageId }));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                matched += page.Count;
                after = (page[^1].ReceivedAt, page[^1].MessageId);
            }

            await db.LegalHolds.Where(h => h.Id == hold.Id).ExecuteUpdateAsync(h => h.SetProperty(x => x.Matched, matched), ct);
            hold.Matched = matched;
        }

        await scope.RecomputeAsync(hold, ct);
        await audit.WriteAsync(new AuditEntry("compliance.hold.create", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "legal_hold",
            hold.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { hold.Name, scope = c.Scope, hold.UserId, hold.OrgUnitId, hold.Query, hold.Reason, hold.Matched })), ct);
        return hold.Id;
    }
}

public sealed record ReleaseLegalHoldCommand(Guid Id) : IRequest<Unit>;

public sealed class ReleaseLegalHoldHandler(BlueMailDbContext db, ICurrentUser me, ComplianceScope scope, AuditWriter audit) : IRequestHandler<ReleaseLegalHoldCommand, Unit>
{
    public async Task<Unit> Handle(ReleaseLegalHoldCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceEdit);
        var hold = await db.LegalHolds.FirstOrDefaultAsync(h => h.Id == c.Id && h.DeletedAt == null, ct) ?? throw AppException.NotFound("hold_not_found");
        if (hold.ReleasedAt is not null)
        {
            throw AppException.Conflict("hold_released");
        }

        hold.ReleasedAt = DateTimeOffset.UtcNow;
        hold.ReleasedBy = me.UserId;
        await db.SaveChangesAsync(ct);
        await scope.RecomputeAsync(hold, ct);
        await audit.WriteAsync(new AuditEntry("compliance.hold.release", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "legal_hold",
            hold.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { hold.Name })), ct);
        return Unit.Value;
    }
}

// ---- retention ----

public sealed record RetentionRuleDto(Guid Id, string Name, string Action, int Days, string? Query, Guid? OrgUnitId, bool IsEnabled, DateTimeOffset? LastRunAt, int LastRunDeleted);

public sealed record ListRetentionRulesQuery : IRequest<IReadOnlyList<RetentionRuleDto>>;

public sealed class ListRetentionRulesHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListRetentionRulesQuery, IReadOnlyList<RetentionRuleDto>>
{
    public async Task<IReadOnlyList<RetentionRuleDto>> Handle(ListRetentionRulesQuery q, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceView);
        return (await db.RetentionRules.AsNoTracking().Where(r => r.DeletedAt == null).OrderBy(r => r.Name).ToListAsync(ct))
            .Select(r => new RetentionRuleDto(r.Id, r.Name, EnumNames.ToUpperSnake(r.Action.ToString()), r.Days, r.Query, r.OrgUnitId, r.IsEnabled, r.LastRunAt, r.LastRunDeleted))
            .ToList();
    }
}

public sealed record SaveRetentionRuleCommand(Guid? Id, string Name, string Action, int Days, string? Query, Guid? OrgUnitId, bool IsEnabled) : IRequest<Guid>;

public sealed class SaveRetentionRuleValidator : AbstractValidator<SaveRetentionRuleCommand>
{
    public SaveRetentionRuleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Action).Must(a => a is "DELETE_AFTER" or "KEEP_MIN").WithErrorCode("invalid_option");
        RuleFor(x => x.Days).InclusiveBetween(1, 36500);
        RuleFor(x => x.Query).MaximumLength(SearchParser.MaxLength);
        // KEEP_MIN applies to everything in its scope (it is checked inside the delete statement, docs/00 D22).
        RuleFor(x => x.Query).Empty().When(x => x.Action == "KEEP_MIN").WithErrorCode("retention_keep_no_query");
    }
}

public sealed class SaveRetentionRuleHandler(BlueMailDbContext db, ICurrentUser me, ComplianceScope scope, AuditWriter audit) : IRequestHandler<SaveRetentionRuleCommand, Guid>
{
    public async Task<Guid> Handle(SaveRetentionRuleCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceEdit);
        _ = await scope.ParseAsync(me.TenantId, c.Query, ct); // a broken query is a 400, not a rule that deletes everything
        if (c.OrgUnitId is { } o && !await db.OrgUnits.AnyAsync(x => x.Id == o && x.DeletedAt == null, ct))
        {
            throw AppException.BadRequest("org_unit_not_found");
        }

        var rule = c.Id is { } id ? await db.RetentionRules.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct) ?? throw AppException.NotFound("retention_not_found")
            : db.RetentionRules.Add(new RetentionRule { TenantId = me.TenantId }).Entity;
        rule.Name = c.Name.Trim();
        rule.Action = c.Action == "KEEP_MIN" ? RetentionAction.KeepMin : RetentionAction.DeleteAfter;
        rule.Days = c.Days;
        rule.Query = string.IsNullOrWhiteSpace(c.Query) ? null : c.Query.Trim();
        rule.OrgUnitId = c.OrgUnitId;
        rule.IsEnabled = c.IsEnabled;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("compliance.retention.save", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "retention_rule",
            rule.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { rule.Name, c.Action, rule.Days, rule.Query, rule.OrgUnitId, rule.IsEnabled })), ct);
        return rule.Id;
    }
}

public sealed record DeleteRetentionRuleCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteRetentionRuleHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<DeleteRetentionRuleCommand, Unit>
{
    public async Task<Unit> Handle(DeleteRetentionRuleCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceEdit);
        var rule = await db.RetentionRules.FirstOrDefaultAsync(r => r.Id == c.Id && r.DeletedAt == null, ct) ?? throw AppException.NotFound("retention_not_found");
        rule.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("compliance.retention.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "retention_rule",
            rule.Id.ToString()), ct);
        return Unit.Value;
    }
}

// ---- eDiscovery ----

public sealed record EDiscoveryHitDto(Guid MessageId, string Mailbox, DateTimeOffset ReceivedAt, string? From, string? Subject, bool HiddenFromUser, bool Held);

public sealed record EDiscoveryResult(IReadOnlyList<EDiscoveryHitDto> Items, int Count, bool CountCapped);

public sealed record EDiscoverySearchQuery(string Query, IReadOnlyList<Guid>? MailboxIds) : IRequest<EDiscoveryResult>;

public sealed class EDiscoverySearchHandler(BlueMailDbContext db, ICurrentUser me, ComplianceScope scope, PgMailStore store, AuditWriter audit)
    : IRequestHandler<EDiscoverySearchQuery, EDiscoveryResult>
{
    public const int CountCap = 10_000;

    public async Task<EDiscoveryResult> Handle(EDiscoverySearchQuery q, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceView);
        var node = await scope.ParseAsync(me.TenantId, q.Query, ct) ?? throw AppException.BadRequest("search_empty");
        var boxes = q.MailboxIds is { Count: > 0 } ? q.MailboxIds : null;
        var hits = await store.ComplianceSearchAsync(me.TenantId, boxes, node, null, includeHidden: true, null, CountCap + 1, ct);
        var preview = hits.Take(100).Select(h => h.MessageId).ToList();
        var rows = await db.Messages.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == me.TenantId && preview.Contains(m.Id))
            .Select(m => new { m.Id, m.FromAddr, m.Subject, m.LegalHoldCount, m.MailboxId }).ToListAsync(ct);
        var boxIds = rows.Select(r => r.MailboxId).Distinct().ToList();
        var names = await (from m in db.Mailboxes.IgnoreQueryFilters()
                           where boxIds.Contains(m.Id)
                           select new
                           {
                               m.Id,
                               Email = db.Users.IgnoreQueryFilters().Where(u => u.Id == m.OwnerId).Select(u => u.PrimaryEmail).FirstOrDefault()
                                       ?? db.SharedMailboxes.IgnoreQueryFilters().Where(s => s.Id == m.OwnerId).Select(s => s.Email).FirstOrDefault(),
                           }).ToDictionaryAsync(x => x.Id, x => x.Email ?? "", ct);
        var byId = rows.ToDictionary(r => r.Id);
        // Every eDiscovery search is recorded (PROMPT 7.5): who looked for what.
        await audit.WriteAsync(new AuditEntry("compliance.ediscovery.search", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "query", null,
            NewValueJson: JsonSerializer.Serialize(new { q.Query, mailboxes = boxes, found = Math.Min(hits.Count, CountCap) })), ct);
        return new EDiscoveryResult(
            hits.Take(100).Where(h => byId.ContainsKey(h.MessageId)).Select(h => new EDiscoveryHitDto(h.MessageId, names.GetValueOrDefault(h.MailboxId, ""), h.ReceivedAt,
                byId[h.MessageId].FromAddr, byId[h.MessageId].Subject, h.HiddenFromUser, byId[h.MessageId].LegalHoldCount > 0)).ToList(),
            Math.Min(hits.Count, CountCap), hits.Count > CountCap);
    }
}

// ---- exports ----

public sealed record DataExportDto(Guid Id, string Kind, string? Subject, string? Query, string Format, string Status, int Total, int Done, long SizeBytes, string? Sha256,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, DateTimeOffset? ExpiresAt);

internal static class Exports
{
    public static DataExportDto Dto(DataExport e, string? subject) => new(e.Id, EnumNames.ToUpperSnake(e.Kind.ToString()), subject, e.Query, e.Format,
        EnumNames.ToUpperSnake(e.Status.ToString()), e.Total, e.Done, e.SizeBytes, e.Sha256, e.Error, e.CreatedAt, e.FinishedAt, e.ExpiresAt);

    /// <summary>One export of a kind at a time per requester: a second click does not start a second full copy.</summary>
    public static async Task<Guid> QueueAsync(BlueMailDbContext db, DataExport e, CancellationToken ct)
    {
        if (await db.DataExports.IgnoreQueryFilters().AnyAsync(x => x.TenantId == e.TenantId && x.RequestedBy == e.RequestedBy && x.Kind == e.Kind
                                                                    && x.SubjectUserId == e.SubjectUserId && (x.Status == JobStatus.Queued || x.Status == JobStatus.Running), ct))
        {
            throw AppException.Conflict("export_running");
        }

        db.DataExports.Add(e);
        await db.SaveChangesAsync(ct);
        return e.Id;
    }
}

/// <summary>"Download my data" (PROMPT 7.7) or, for compliance administrators, all data of one user (GDPR access request).</summary>
public sealed record RequestUserExportCommand(Guid? UserId) : IRequest<Guid>;

public sealed class RequestUserExportHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<RequestUserExportCommand, Guid>
{
    public async Task<Guid> Handle(RequestUserExportCommand c, CancellationToken ct)
    {
        var subject = c.UserId ?? me.UserId;
        if (subject != me.UserId)
        {
            me.Require(Permissions.AdminComplianceEdit);
        }

        if (!await db.Users.AnyAsync(u => u.Id == subject && u.DeletedAt == null, ct))
        {
            throw AppException.NotFound("user_not_found");
        }

        var id = await Exports.QueueAsync(db, new DataExport
        {
            Id = Guid.NewGuid(), TenantId = me.TenantId, Kind = ExportKind.UserData, RequestedBy = me.UserId, SubjectUserId = subject, Format = "MBOX",
            Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await audit.WriteAsync(new AuditEntry("export.user.request", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", subject.ToString()), ct);
        return id;
    }
}

public sealed record RequestEDiscoveryExportCommand(string Query, IReadOnlyList<Guid>? MailboxIds, string Format) : IRequest<Guid>;

public sealed class RequestEDiscoveryExportValidator : AbstractValidator<RequestEDiscoveryExportCommand>
{
    public RequestEDiscoveryExportValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(SearchParser.MaxLength);
        RuleFor(x => x.Format).Must(f => f is "MBOX" or "EML").WithErrorCode("invalid_option");
    }
}

public sealed class RequestEDiscoveryExportHandler(BlueMailDbContext db, ICurrentUser me, ComplianceScope scope, AuditWriter audit)
    : IRequestHandler<RequestEDiscoveryExportCommand, Guid>
{
    public async Task<Guid> Handle(RequestEDiscoveryExportCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminComplianceEdit);
        _ = await scope.ParseAsync(me.TenantId, c.Query, ct);
        var id = await Exports.QueueAsync(db, new DataExport
        {
            Id = Guid.NewGuid(), TenantId = me.TenantId, Kind = ExportKind.EDiscovery, RequestedBy = me.UserId, Query = c.Query.Trim(),
            Mailboxes = JsonSerializer.Serialize(c.MailboxIds ?? []), Format = c.Format, Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await audit.WriteAsync(new AuditEntry("compliance.ediscovery.export", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "export",
            id.ToString(), NewValueJson: JsonSerializer.Serialize(new { c.Query, mailboxes = c.MailboxIds, c.Format })), ct);
        return id;
    }
}

/// <summary>Whole-tenant export at the end of a contract (PROMPT 7.7), by the platform administrator.</summary>
public sealed record RequestTenantExportCommand(Guid TenantId) : IRequest<Guid>;

public sealed class RequestTenantExportHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<RequestTenantExportCommand, Guid>
{
    public async Task<Guid> Handle(RequestTenantExportCommand c, CancellationToken ct)
    {
        me.Require(Permissions.PlatformTenantsEdit);
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == c.TenantId && t.DeletedAt == null, ct))
        {
            throw AppException.NotFound("tenant_not_found");
        }

        var id = await Exports.QueueAsync(db, new DataExport
        {
            Id = Guid.NewGuid(), TenantId = c.TenantId, Kind = ExportKind.Tenant, RequestedBy = me.UserId, Format = "MBOX", Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await audit.WriteAsync(new AuditEntry("export.tenant.request", AuditResult.Success, c.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "tenant",
            c.TenantId.ToString()), ct);
        return id;
    }
}

public sealed record ListExportsQuery(bool Admin) : IRequest<IReadOnlyList<DataExportDto>>;

public sealed class ListExportsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListExportsQuery, IReadOnlyList<DataExportDto>>
{
    public async Task<IReadOnlyList<DataExportDto>> Handle(ListExportsQuery q, CancellationToken ct)
    {
        var query = db.DataExports.IgnoreQueryFilters().AsNoTracking().Where(e => e.RequestedBy == me.UserId);
        if (q.Admin)
        {
            me.Require(Permissions.AdminComplianceView);
            query = db.DataExports.AsNoTracking().Where(e => e.Kind != ExportKind.UserData || e.RequestedBy != e.SubjectUserId);
        }

        var rows = await (from e in query
                          orderby e.CreatedAt descending
                          select new { e, Subject = db.Users.IgnoreQueryFilters().Where(u => u.Id == e.SubjectUserId).Select(u => u.PrimaryEmail).FirstOrDefault() })
            .Take(200).ToListAsync(ct);
        return rows.Select(r => Exports.Dto(r.e, r.Subject)).ToList();
    }
}

/// <summary>A download link for a finished export: signed, for the requester only, valid two hours (PROMPT 7.7 "liên kết có hạn").</summary>
public sealed record ExportLinkQuery(Guid Id) : IRequest<string>;

public sealed class ExportLinkHandler(BlueMailDbContext db, ICurrentUser me, SignedUrls signer, AuditWriter audit) : IRequestHandler<ExportLinkQuery, string>
{
    public async Task<string> Handle(ExportLinkQuery q, CancellationToken ct)
    {
        var e = await db.DataExports.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == q.Id && x.RequestedBy == me.UserId, ct)
                ?? throw AppException.NotFound("export_not_found");
        if (e.Status != JobStatus.Done || e.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw AppException.Conflict("export_not_ready");
        }

        await audit.WriteAsync(new AuditEntry("export.download", AuditResult.Success, e.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "export", e.Id.ToString()), ct);
        return "/api/v1/exports/d/" + signer.Sign(SignedUrls.Grant(SignedKind.Export, e.TenantId, me.UserId, Guid.Empty, e.Id, download: true));
    }
}

/// <summary>Streams a finished export for a valid link (anonymous endpoint: the signed token is the credential).</summary>
public sealed class ExportDownloads(BlueMailDbContext db, SignedUrls signer, ExportStore exports)
{
    public async Task<(string FileName, long Size, Func<Stream, CancellationToken, Task> Write)?> OpenAsync(string token, CancellationToken ct)
    {
        var g = signer.Verify(token);
        if (g is null || g.Kind != SignedKind.Export)
        {
            return null;
        }

        var e = await db.DataExports.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == g.MessageId && x.TenantId == g.TenantId && x.RequestedBy == g.UserId, ct);
        var active = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == g.UserId && u.Status == UserStatus.Active && u.DeletedAt == null, ct);
        if (e is null || !active || e.Status != JobStatus.Done || e.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        var name = $"bluemail-{EnumNames.ToUpperSnake(e.Kind.ToString()).ToLowerInvariant()}-{e.CreatedAt.UtcDateTime:yyyyMMdd-HHmm}.zip";
        return (name, e.SizeBytes, (output, t) => exports.CopyToAsync(e.TenantId, e.Id, e.Chunks, output, t));
    }
}
