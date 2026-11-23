using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Antispam;

/// <summary>
/// "Report phishing" (PROMPT 6.9): the message goes to Spam, Bayes learns it, and a copy (the original as a
/// message/rfc822 attachment) is delivered to the organisation's security mailbox when one is configured.
/// The address must be a mailbox of the tenant: reports never leave the organisation.
/// </summary>
public sealed class PhishingReports(BlueMailDbContext db, PgMailStore store, RecipientResolver resolver, BmSettings settings, ILogger<PhishingReports> log)
{
    public async Task ForwardAsync(MailboxAccess access, IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        var address = await db.SpamSettings.IgnoreQueryFilters().Where(s => s.TenantId == access.TenantId).Select(s => s.PhishingReportAddress).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var target = await resolver.ResolveAsync(address, 0, ct);
        if (target.Status != RecipientStatus.Ok || target.TenantId != access.TenantId)
        {
            log.LogWarning("Phishing report address {Address} of tenant {Tenant} is not a mailbox of the tenant", address, access.TenantId);
            return;
        }

        var reporter = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == access.ActorUserId).Select(u => u.PrimaryEmail).FirstAsync(ct);
        foreach (var id in messageIds.Take(50))
        {
            var original = await store.GetMessageAsync(access, id, ct);
            using var raw = new MemoryStream();
            if (original is null || !await store.CopyRawAsync(access, id, raw, ct))
            {
                continue;
            }

            var bytes = raw.ToArray();
            var report = new MessageBuilder
            {
                From = new MailboxAddress("BlueMail", "postmaster@" + settings.MailHostname),
                To = [new MailboxAddress(null, address)],
                Subject = "[Phishing report] " + (original.Subject ?? ""),
                MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
                Date = DateTimeOffset.UtcNow,
                TextBody = $"Reported as phishing by {reporter} on {DateTimeOffset.UtcNow:u}.\r\nOriginal sender: {original.From?.Address}\r\nThe original message is attached unchanged.\r\n",
                Attachments = [new BuilderAttachment { FileName = "reported.eml", ContentType = "message/rfc822", Open = () => new MemoryStream(bytes, writable: false) }],
                ExtraHeaders = [("Auto-Submitted", "auto-generated")],
            }.ToBytes();
            foreach (var mb in target.MailboxIds)
            {
                await OutgoingDispatcher.DeliverRawAsync(store, access.TenantId, report, new MailboxDelivery(mb, [SystemLabels.Inbox]), ct);
            }
        }
    }
}

// ---------- tenant spam settings ----------

public sealed record SpamSettingsDto(double SpamThreshold, double RejectThreshold, bool Greylisting, string SuspiciousAttachments, string? PhishingReportAddress,
    IReadOnlyList<DnsblDto> Lists);

public sealed record DnsblDto(int Id, string Name, string Zone, string Kind, double Score, bool Enabled, bool Platform, string LicenseNote);

public sealed record GetSpamSettingsQuery : IRequest<SpamSettingsDto>;

public sealed class GetSpamSettingsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<GetSpamSettingsQuery, SpamSettingsDto>
{
    public async Task<SpamSettingsDto> Handle(GetSpamSettingsQuery q, CancellationToken ct)
    {
        var s = await db.SpamSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SpamSettings { TenantId = me.TenantId };
        var lists = await db.DnsblSources.AsNoTracking().Where(l => l.TenantId == null || l.TenantId == me.TenantId).OrderBy(l => l.Id)
            .Select(l => new DnsblDto(l.Id, l.Name, l.Zone, l.Kind.ToString().ToUpperInvariant(), l.Score, l.Enabled, l.TenantId == null, l.LicenseNote)).ToListAsync(ct);
        return new SpamSettingsDto(s.SpamThreshold, s.RejectThreshold, s.Greylisting, EnumNames.ToUpperSnake(s.SuspiciousAttachments.ToString()), s.PhishingReportAddress, lists);
    }
}

public sealed record SaveSpamSettingsCommand(double SpamThreshold, double RejectThreshold, bool Greylisting, string SuspiciousAttachments, string? PhishingReportAddress)
    : IRequest<Unit>;

public sealed class SaveSpamSettingsValidator : AbstractValidator<SaveSpamSettingsCommand>
{
    public SaveSpamSettingsValidator()
    {
        RuleFor(x => x.SpamThreshold).InclusiveBetween(1, 30).WithErrorCode("spam_threshold_range");
        RuleFor(x => x.RejectThreshold).GreaterThan(x => x.SpamThreshold).WithErrorCode("reject_threshold_above_spam").LessThanOrEqualTo(100);
        RuleFor(x => x.SuspiciousAttachments).Must(v => v is "SPAM" or "QUARANTINE" or "REJECT").WithErrorCode("invalid_option");
        RuleFor(x => x.PhishingReportAddress).MaximumLength(320).WithErrorCode(ValidationCodes.TooLong);
    }
}

public sealed class SaveSpamSettingsHandler(BlueMailDbContext db, ICurrentUser me, RecipientResolver resolver, AuditWriter audit) : IRequestHandler<SaveSpamSettingsCommand, Unit>
{
    public async Task<Unit> Handle(SaveSpamSettingsCommand c, CancellationToken ct)
    {
        var address = string.IsNullOrWhiteSpace(c.PhishingReportAddress) ? null : c.PhishingReportAddress.Trim().ToLowerInvariant();
        if (address is not null)
        {
            var r = await resolver.ResolveAsync(address, 0, ct);
            if (r.Status != RecipientStatus.Ok || r.TenantId != me.TenantId)
            {
                throw AppException.BadRequest("phishing_address_not_internal");
            }
        }

        var s = await db.SpamSettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new SpamSettings { TenantId = me.TenantId };
            db.SpamSettings.Add(s);
        }

        s.SpamThreshold = c.SpamThreshold;
        s.RejectThreshold = c.RejectThreshold;
        s.Greylisting = c.Greylisting;
        s.SuspiciousAttachments = Enum.Parse<SuspiciousAttachmentAction>(EnumNames.FromUpperSnake(c.SuspiciousAttachments), true);
        s.PhishingReportAddress = address;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("spam.settings.update", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "spam_settings",
            me.TenantId.ToString(), NewValueJson: JsonSerializer.Serialize(c)), ct);
        return Unit.Value;
    }
}

/// <summary>Turns a block list on or off for the tenant. Platform lists are copied as tenant rows when changed.</summary>
public sealed record ToggleDnsblCommand(int Id, bool Enabled) : IRequest<Unit>;

public sealed class ToggleDnsblHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<ToggleDnsblCommand, Unit>
{
    public async Task<Unit> Handle(ToggleDnsblCommand c, CancellationToken ct)
    {
        var source = await db.DnsblSources.FirstOrDefaultAsync(l => l.Id == c.Id && (l.TenantId == null || l.TenantId == me.TenantId), ct)
                     ?? throw AppException.NotFound("list_not_found");
        if (source.TenantId is null)
        {
            // A tenant cannot change the platform row; it gets its own copy with the chosen state.
            var own = await db.DnsblSources.FirstOrDefaultAsync(l => l.TenantId == me.TenantId && l.Zone == source.Zone, ct);
            if (own is null)
            {
                own = new DnsblSource { TenantId = me.TenantId, Name = source.Name, Zone = source.Zone, Kind = source.Kind, Score = source.Score, LicenseNote = source.LicenseNote };
                db.DnsblSources.Add(own);
            }

            source = own;
        }

        source.Enabled = c.Enabled;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("spam.dnsbl." + (c.Enabled ? "enable" : "disable"), AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip,
            me.UserAgent, "dnsbl", source.Zone), ct);
        return Unit.Value;
    }
}

// ---------- quarantine ----------

public sealed record QuarantineItemDto(Guid Id, DateTimeOffset CreatedAt, string? FromAddress, string? EnvelopeFrom, string? Subject, string Reason, double? SpamScore,
    int Recipients, string Status, string? Report);

public sealed record ListQuarantineQuery(string? Status, int Page, int PageSize) : IRequest<Paged<QuarantineItemDto>>;

public sealed class ListQuarantineHandler(BlueMailDbContext db) : IRequestHandler<ListQuarantineQuery, Paged<QuarantineItemDto>>
{
    public async Task<Paged<QuarantineItemDto>> Handle(ListQuarantineQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var status = q.Status?.ToUpperInvariant() switch { "RELEASED" => QuarantineStatus.Released, "DELETED" => QuarantineStatus.Deleted, _ => QuarantineStatus.Held };
        var query = db.Quarantine.AsNoTracking().Where(x => x.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<QuarantineItemDto>(rows.Select(x => new QuarantineItemDto(x.Id, x.CreatedAt, x.FromAddress, x.EnvelopeFrom, x.Subject, x.Reason, x.SpamScore,
            JsonSerializer.Deserialize<List<Guid>>(x.Recipients)?.Count ?? 0, EnumNames.ToUpperSnake(x.Status.ToString()), x.Report)).ToList(), total, page, size);
    }
}

public sealed record DecideQuarantineCommand(Guid Id, bool Release) : IRequest<Unit>;

/// <summary>Release delivers the held copy to its recipients' Inbox; delete discards it. Both are audited.</summary>
public sealed class DecideQuarantineHandler(BlueMailDbContext db, PgMailStore store, BlobStore blobs, ICurrentUser me, AuditWriter audit)
    : IRequestHandler<DecideQuarantineCommand, Unit>
{
    public async Task<Unit> Handle(DecideQuarantineCommand c, CancellationToken ct)
    {
        var item = await db.Quarantine.FirstOrDefaultAsync(x => x.Id == c.Id && x.Status == QuarantineStatus.Held, ct) ?? throw AppException.NotFound("quarantine_not_found");
        // Claim first: two administrators clicking "release" at once must not deliver twice.
        var claimed = await db.Quarantine.Where(x => x.Id == item.Id && x.Status == QuarantineStatus.Held).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.Status, c.Release ? QuarantineStatus.Released : QuarantineStatus.Deleted)
            .SetProperty(y => y.DecidedBy, me.UserId).SetProperty(y => y.DecidedAt, DateTimeOffset.UtcNow), ct);
        if (claimed == 0)
        {
            throw AppException.Conflict("quarantine_already_decided");
        }

        if (c.Release)
        {
            using var raw = new MemoryStream();
            await blobs.ReadAsync(item.TenantId, item.BlobSha256, raw, ct: ct);
            var bytes = raw.ToArray();
            foreach (var mb in JsonSerializer.Deserialize<List<Guid>>(item.Recipients) ?? [])
            {
                await OutgoingDispatcher.DeliverRawAsync(store, item.TenantId, bytes, new MailboxDelivery(mb, [SystemLabels.Inbox]), ct);
            }
        }

        await audit.WriteAsync(new AuditEntry(c.Release ? "quarantine.release" : "quarantine.delete", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip,
            me.UserAgent, "quarantine", item.Id.ToString(), NewValueJson: JsonSerializer.Serialize(new { item.Subject, item.FromAddress, item.Reason })), ct);
        return Unit.Value;
    }
}
