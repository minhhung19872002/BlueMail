using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Rules;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Admin.Domains;

public sealed record DomainDto(Guid Id, string Domain, bool IsPrimary, bool Verified, string? AliasOf, string? CatchAllTarget, bool MxOk, bool SpfOk, bool DkimOk,
    bool DmarcOk, bool MtaStsOk, DateTimeOffset? LastCheckedAt, IReadOnlyList<DkimKeyDto> DkimKeys);

public sealed record DkimKeyDto(Guid Id, string Selector, string Status, DateTimeOffset? ActivatedAt, DateTimeOffset? RetireAt);

public sealed record DomainRecordsDto(DomainDto Domain, IReadOnlyList<DnsRecordCheck> Records);

/// <summary>Domain checks shared by the wizard, the periodic worker and the DKIM rotation.</summary>
public sealed class DomainService(BlueMailDbContext db, DomainRecordPlanner planner, IDomainDns dns, DkimKeyService dkim)
{
    public async Task<IReadOnlyList<DomainRecordPlanner.DkimPlan>> DkimPlansAsync(Guid domainId, CancellationToken ct) =>
        (await db.DkimKeys.IgnoreQueryFilters().AsNoTracking()
            .Where(k => k.DomainId == domainId && k.DeletedAt == null && k.Status != DkimKeyStatus.Retired).OrderBy(k => k.CreatedAt).ToListAsync(ct))
        .Select(k => new DomainRecordPlanner.DkimPlan(k.Selector, k.PublicKey, EnumNames.ToUpperSnake(k.Status.ToString()))).ToList();

    /// <summary>Runs every check, stores the result on the domain and returns it. Verification is set the first time the TXT is seen.</summary>
    public async Task<IReadOnlyList<DnsRecordCheck>> CheckAsync(TenantDomain d, CancellationToken ct)
    {
        var plans = await DkimPlansAsync(d.Id, ct);
        var checks = await planner.CheckAsync(dns, d.Domain, d.VerificationToken, plans, d.VerifiedAt ?? d.CreatedAt, ct, d.VerifiedAt != null);
        bool Ok(RecordKind k) => checks.Where(c => c.Kind == k).All(c => c.State == RecordState.Ok) && checks.Any(c => c.Kind == k);
        if (d.VerifiedAt is null && Ok(RecordKind.Verification))
        {
            d.VerifiedAt = DateTimeOffset.UtcNow;
        }

        d.MxOk = Ok(RecordKind.Mx);
        d.SpfOk = Ok(RecordKind.Spf);
        d.DkimOk = checks.Any(c => c.Kind == RecordKind.Dkim && c.Required && c.State == RecordState.Ok);
        d.DmarcOk = Ok(RecordKind.Dmarc);
        d.MtaStsOk = Ok(RecordKind.MtaSts) && (Ok(RecordKind.MtaStsHost) || checks.All(c => c.Kind != RecordKind.MtaStsHost));
        d.LastCheckedAt = DateTimeOffset.UtcNow;
        d.LastCheck = JsonSerializer.Serialize(checks, ApiJson.Options);

        // DKIM rotation: a pending key whose record is now in DNS becomes the active one; the old key keeps its record
        // for 7 days so mail already on its way still verifies (PROMPT 7.2 step 5).
        var pending = await db.DkimKeys.IgnoreQueryFilters().Where(k => k.DomainId == d.Id && k.Status == DkimKeyStatus.Pending && k.DeletedAt == null).ToListAsync(ct);
        foreach (var p in pending.Where(p => checks.Any(c => c.Kind == RecordKind.Dkim && c.Name.StartsWith(p.Selector + "._domainkey.", StringComparison.Ordinal) && c.State == RecordState.Ok)))
        {
            var now = DateTimeOffset.UtcNow;
            await db.DkimKeys.IgnoreQueryFilters().Where(k => k.DomainId == d.Id && k.Status == DkimKeyStatus.Active && k.Id != p.Id)
                .ExecuteUpdateAsync(k => k.SetProperty(x => x.Status, DkimKeyStatus.Retiring).SetProperty(x => x.RetireAt, now.AddDays(7)), ct);
            p.Status = DkimKeyStatus.Active;
            p.ActivatedAt = now;
        }

        await db.DkimKeys.IgnoreQueryFilters().Where(k => k.DomainId == d.Id && k.Status == DkimKeyStatus.Retiring && k.RetireAt < DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(k => k.SetProperty(x => x.Status, DkimKeyStatus.Retired), ct);
        await db.SaveChangesAsync(ct);
        return checks;
    }

    public async Task<DomainDto> ToDtoAsync(TenantDomain d, CancellationToken ct)
    {
        var keys = await db.DkimKeys.IgnoreQueryFilters().AsNoTracking().Where(k => k.DomainId == d.Id && k.DeletedAt == null).OrderByDescending(k => k.CreatedAt)
            .Select(k => new DkimKeyDto(k.Id, k.Selector, EnumNames.ToUpperSnake(k.Status.ToString()), k.ActivatedAt, k.RetireAt)).ToListAsync(ct);
        var aliasOf = d.AliasOfDomainId is { } a ? await db.Domains.IgnoreQueryFilters().Where(x => x.Id == a).Select(x => x.Domain).FirstOrDefaultAsync(ct) : null;
        return new DomainDto(d.Id, d.Domain, d.IsPrimary, d.VerifiedAt is not null, aliasOf, d.CatchAllTarget, d.MxOk, d.SpfOk, d.DkimOk, d.DmarcOk, d.MtaStsOk,
            d.LastCheckedAt, keys);
    }

    /// <summary>A key waiting for its DNS record (rotation), with a new selector.</summary>
    public async Task<DkimKey> CreatePendingKeyAsync(TenantDomain d, CancellationToken ct)
    {
        using var rsa = RSA.Create(2048);
        // Stored as PENDING from the start: "one active key per domain" is a database rule.
        return await dkim.StoreAsync(d.TenantId, d.Id, "bm" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), rsa, ct, DkimKeyStatus.Pending);
    }
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

public sealed record ListDomainsQuery : IRequest<IReadOnlyList<DomainDto>>;

public sealed class ListDomainsHandler(BlueMailDbContext db, DomainService domains) : IRequestHandler<ListDomainsQuery, IReadOnlyList<DomainDto>>
{
    public async Task<IReadOnlyList<DomainDto>> Handle(ListDomainsQuery q, CancellationToken ct)
    {
        var rows = await db.Domains.AsNoTracking().OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Domain).ToListAsync(ct);
        var list = new List<DomainDto>();
        foreach (var d in rows)
        {
            list.Add(await domains.ToDtoAsync(d, ct));
        }

        return list;
    }
}

public sealed record GetDomainRecordsQuery(Guid Id, bool CheckNow) : IRequest<DomainRecordsDto>;

public sealed class GetDomainRecordsHandler(BlueMailDbContext db, DomainService domains, DomainRecordPlanner planner) : IRequestHandler<GetDomainRecordsQuery, DomainRecordsDto>
{
    public async Task<DomainRecordsDto> Handle(GetDomainRecordsQuery q, CancellationToken ct)
    {
        var d = await db.Domains.FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw AppException.NotFound("domain_not_found");
        IReadOnlyList<DnsRecordCheck> records;
        if (q.CheckNow)
        {
            records = await domains.CheckAsync(d, ct);
        }
        else
        {
            // The current plan (a key added by a rotation shows at once), with what the last check saw for each record.
            var last = d.LastCheck is null ? [] : JsonSerializer.Deserialize<List<DnsRecordCheck>>(d.LastCheck, ApiJson.Options) ?? [];
            records = planner.Plan(d.Domain, d.VerificationToken, await domains.DkimPlansAsync(d.Id, ct), d.VerifiedAt ?? d.CreatedAt, d.VerifiedAt != null)
                .Select(p => last.FirstOrDefault(c => c.Kind == p.Kind && c.Name == p.Name && c.Expected == p.Expected)
                             ?? new DnsRecordCheck(p.Kind, p.Type, p.Name, p.Expected, p.Required, RecordState.Unknown, [])).ToList();
        }

        return new DomainRecordsDto(await domains.ToDtoAsync(d, ct), records);
    }
}

public sealed record AddDomainCommand(string Domain, Guid? AliasOfDomainId) : IRequest<Guid>;

public sealed class AddDomainValidator : AbstractValidator<AddDomainCommand>
{
    public AddDomainValidator() => RuleFor(x => x.Domain).NotEmpty().MaximumLength(253).Must(AddDomainHandler.IsDomain).WithErrorCode(ValidationCodes.InvalidFormat);
}

public sealed class AddDomainHandler(BlueMailDbContext db, ICurrentUser me, DkimKeyService dkim, AuditWriter audit) : IRequestHandler<AddDomainCommand, Guid>
{
    private static readonly IdnMapping Idn = new();

    /// <summary>A host name (internationalised names allowed, stored as A-labels).</summary>
    public static bool IsDomain(string s)
    {
        try
        {
            var a = Idn.GetAscii(s.Trim().TrimEnd('.').ToLowerInvariant());
            return a.Length <= 253 && a.Contains('.', StringComparison.Ordinal) && a.Split('.').All(l => l.Length is > 0 and <= 63 && !l.StartsWith('-') && !l.EndsWith('-')
                && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<Guid> Handle(AddDomainCommand c, CancellationToken ct)
    {
        var name = Idn.GetAscii(c.Domain.Trim().TrimEnd('.').ToLowerInvariant());
        if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Domain == name && d.DeletedAt == null, ct))
        {
            throw AppException.Conflict("domain_taken"); // one domain, one tenant, system-wide
        }

        if (c.AliasOfDomainId is { } target && !await db.Domains.AnyAsync(d => d.Id == target && d.AliasOfDomainId == null, ct))
        {
            throw AppException.BadRequest("domain_not_found");
        }

        var d = new TenantDomain
        {
            TenantId = me.TenantId,
            Domain = name,
            VerificationToken = DomainRecordPlanner.VerificationPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            AliasOfDomainId = c.AliasOfDomainId,
        };
        db.Domains.Add(d);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("domain_taken");
        }

        await dkim.GetOrCreateAsync(me.TenantId, name, ct); // the DKIM record is shown at once (PROMPT 7.2 step 2)
        await audit.WriteAsync(new AuditEntry("domain.add", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "domain", name), ct);
        return d.Id;
    }
}

public sealed record DeleteDomainCommand(Guid Id) : IRequest<Unit>;

public sealed class DeleteDomainHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<DeleteDomainCommand, Unit>
{
    public async Task<Unit> Handle(DeleteDomainCommand c, CancellationToken ct)
    {
        var d = await db.Domains.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw AppException.NotFound("domain_not_found");
        var suffix = "@" + d.Domain;
        if (d.IsPrimary || await db.Users.AnyAsync(u => u.PrimaryEmail.EndsWith(suffix) && u.DeletedAt == null, ct)
                        || await db.UserAliases.AnyAsync(a => a.Email.EndsWith(suffix), ct) || await db.Groups.AnyAsync(g => g.Email.EndsWith(suffix), ct)
                        || await db.SharedMailboxes.AnyAsync(s => s.Email.EndsWith(suffix), ct) || await db.Domains.AnyAsync(x => x.AliasOfDomainId == d.Id, ct))
        {
            throw AppException.Conflict("domain_in_use");
        }

        d.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("domain.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "domain", d.Domain), ct);
        return Unit.Value;
    }
}

public sealed record SetCatchAllCommand(Guid Id, string? Target) : IRequest<Unit>;

public sealed class SetCatchAllHandler(BlueMailDbContext db, ICurrentUser me, RecipientResolver resolver, AuditWriter audit) : IRequestHandler<SetCatchAllCommand, Unit>
{
    public async Task<Unit> Handle(SetCatchAllCommand c, CancellationToken ct)
    {
        var d = await db.Domains.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw AppException.NotFound("domain_not_found");
        var target = string.IsNullOrWhiteSpace(c.Target) ? null : c.Target.Trim().ToLowerInvariant();
        if (target is not null)
        {
            var r = await resolver.ResolveAsync(target, 0, ct);
            if (r.Status != RecipientStatus.Ok || r.TenantId != me.TenantId)
            {
                throw AppException.BadRequest("catch_all_not_internal");
            }
        }

        d.CatchAllTarget = target;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("domain.catch_all", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "domain", d.Domain,
            NewValueJson: JsonSerializer.Serialize(new { target })), ct);
        return Unit.Value;
    }
}

public sealed record RotateDkimCommand(Guid DomainId) : IRequest<DkimKeyDto>;

public sealed class RotateDkimHandler(BlueMailDbContext db, ICurrentUser me, DomainService domains, AuditWriter audit) : IRequestHandler<RotateDkimCommand, DkimKeyDto>
{
    public async Task<DkimKeyDto> Handle(RotateDkimCommand c, CancellationToken ct)
    {
        var d = await db.Domains.FirstOrDefaultAsync(x => x.Id == c.DomainId, ct) ?? throw AppException.NotFound("domain_not_found");
        if (await db.DkimKeys.AnyAsync(k => k.DomainId == d.Id && k.Status == DkimKeyStatus.Pending && k.DeletedAt == null, ct))
        {
            throw AppException.Conflict("rotation_in_progress");
        }

        var key = await domains.CreatePendingKeyAsync(d, ct);
        await audit.WriteAsync(new AuditEntry("dkim.rotate", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "domain", d.Domain,
            NewValueJson: JsonSerializer.Serialize(new { key.Selector })), ct);
        return new DkimKeyDto(key.Id, key.Selector, "PENDING", null, null);
    }
}

/// <summary>
/// Re-checks every domain's DNS every 6 hours (PROMPT 7.2 step 3), moves DKIM rotations along, and tells the tenant's
/// administrators by mail when a required record that was right goes wrong.
/// </summary>
public sealed class DomainWatchWorker(IServiceScopeFactory scopes, ILogger<DomainWatchWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow - Interval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Domain check round failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    /// <summary>Checks domains last checked before <paramref name="olderThan"/>; returns how many broke.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<DomainService>();
        var due = await db.Domains.IgnoreQueryFilters().Where(d => d.DeletedAt == null && (d.LastCheckedAt == null || d.LastCheckedAt < olderThan)).Take(200).ToListAsync(ct);
        var broke = 0;
        foreach (var d in due)
        {
            var before = (d.MxOk, d.SpfOk, d.DkimOk);
            await service.CheckAsync(d, ct);
            var lost = new List<string>();
            if (before.MxOk && !d.MxOk) lost.Add("MX");
            if (before.SpfOk && !d.SpfOk) lost.Add("SPF");
            if (before.DkimOk && !d.DkimOk) lost.Add("DKIM");
            if (lost.Count > 0)
            {
                broke++;
                await NotifyAsync(scope.ServiceProvider, d, lost, ct);
            }
        }

        return broke;
    }

    private static async Task NotifyAsync(IServiceProvider sp, TenantDomain d, IReadOnlyList<string> lost, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var settings = sp.GetRequiredService<BmSettings>();
        var mail = sp.GetRequiredService<SystemMail>();
        var admins = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == d.TenantId && u.IsTenantAdmin && u.DeletedAt == null && u.Status == UserStatus.Active).ToListAsync(ct);
        foreach (var a in admins)
        {
            var culture = CultureInfo.GetCultureInfo(a.Locale ?? "en");
            var args = new Dictionary<string, object?> { ["domain"] = d.Domain, ["records"] = string.Join(", ", lost) };
            var bytes = new MessageBuilder
            {
                From = new MailboxAddress("BlueMail", "postmaster@" + settings.MailHostname),
                To = [new MailboxAddress(a.DisplayName, a.PrimaryEmail)],
                Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("domain.broken.subject", culture), culture, args),
                MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
                Date = DateTimeOffset.UtcNow,
                TextBody = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get("domain.broken.body", culture), culture, args),
                ExtraHeaders = [("Auto-Submitted", "auto-generated")],
            }.ToBytes();
            await mail.SendAsync(d.TenantId, null, "", a.PrimaryEmail, bytes, notification: true, a.Locale, null, ct);
        }
    }
}
