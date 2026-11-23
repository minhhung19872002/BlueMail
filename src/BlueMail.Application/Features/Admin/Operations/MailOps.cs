using System.Net;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Operations;

// ---------- message trace (PROMPT 7.6) ----------

public sealed record TraceQuery(string? Sender, string? Recipient, string? MessageId, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
    : IRequest<Paged<TraceRowDto>>;

public sealed record TraceRowDto(long Id, DateTimeOffset At, string Direction, string Status, string? EnvelopeFrom, string Rcpt, string? MessageId, long Size,
    string? RemoteIp, string? RemoteHost, string? SmtpResponse, double? SpamScore);

public sealed class TraceHandler(BlueMailDbContext db) : IRequestHandler<TraceQuery, Paged<TraceRowDto>>
{
    public async Task<Paged<TraceRowDto>> Handle(TraceQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var query = MailLogFilter.Apply(db.MailLog.AsNoTracking(), q.Sender, q.Recipient, q.MessageId, q.From, q.To);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(l => l.OccurredAt).ThenByDescending(l => l.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<TraceRowDto>(rows.Select(Map).ToList(), total, page, size);
    }

    public static TraceRowDto Map(MailLogEntry l) => new(l.Id, l.OccurredAt, EnumNames.ToUpperSnake(l.Direction.ToString()), EnumNames.ToUpperSnake(l.Status.ToString()),
        l.EnvelopeFrom, l.Rcpt, l.RfcMessageId, l.Size, l.RemoteIp, l.RemoteHost, l.SmtpResponse, l.SpamScore);
}

public static class MailLogFilter
{
    public static IQueryable<MailLogEntry> Apply(IQueryable<MailLogEntry> q, string? sender, string? rcpt, string? messageId, DateTimeOffset? from, DateTimeOffset? to)
    {
        static string Like(string s) => "%" + s.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        if (!string.IsNullOrWhiteSpace(sender))
        {
            var p = Like(sender);
            q = q.Where(l => EF.Functions.ILike(l.EnvelopeFrom ?? "", p));
        }

        if (!string.IsNullOrWhiteSpace(rcpt))
        {
            var p = Like(rcpt);
            q = q.Where(l => EF.Functions.ILike(l.Rcpt, p));
        }

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            var id = messageId.Trim().Trim('<', '>');
            q = q.Where(l => l.RfcMessageId == id);
        }

        if (from is { } f)
        {
            q = q.Where(l => l.OccurredAt >= f);
        }

        if (to is { } t)
        {
            q = q.Where(l => l.OccurredAt < t);
        }

        return q;
    }
}

/// <summary>Everything known about one logged hop: authentication, spam report, where it was stored; for outbound mail the queue state.</summary>
public sealed record TraceDetailDto(TraceRowDto Row, JsonElement? Details, IReadOnlyList<StoredCopyDto> StoredIn, IReadOnlyList<QueueItemDto> Queue);

public sealed record StoredCopyDto(string Mailbox, IReadOnlyList<string> Labels, JsonElement? AuthResults, double? SpamScore, JsonElement? SpamReport);

public sealed record GetTraceDetailQuery(long Id) : IRequest<TraceDetailDto>;

public sealed class GetTraceDetailHandler(BlueMailDbContext db) : IRequestHandler<GetTraceDetailQuery, TraceDetailDto>
{
    public async Task<TraceDetailDto> Handle(GetTraceDetailQuery q, CancellationToken ct)
    {
        var l = await db.MailLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw AppException.NotFound("not_found");
        static JsonElement? J(string? s) => s is null ? null : JsonDocument.Parse(s).RootElement.Clone();
        var stored = new List<StoredCopyDto>();
        if (l.RfcMessageId is { } mid)
        {
            var copies = await (from m in db.Messages.AsNoTracking()
                                join b in db.Mailboxes.AsNoTracking() on m.MailboxId equals b.Id
                                where m.RfcMessageId == mid
                                select new
                                {
                                    m.Id, m.AuthResults, m.SpamScore, m.SpamReport,
                                    Owner = db.Users.Where(u => u.Id == b.OwnerId).Select(u => u.PrimaryEmail).FirstOrDefault()
                                            ?? db.SharedMailboxes.Where(s => s.Id == b.OwnerId).Select(s => s.Email).FirstOrDefault(),
                                    Labels = (from ml in db.MessageLabels where ml.MessageId == m.Id join lb in db.Labels on ml.LabelId equals lb.Id select lb.SystemKey ?? lb.Name).ToList(),
                                }).Take(50).ToListAsync(ct);
            stored.AddRange(copies.Select(c => new StoredCopyDto(c.Owner ?? "", c.Labels, J(c.AuthResults), c.SpamScore, J(c.SpamReport))));
        }

        var queue = l.Direction == MailDirection.Outbound && l.RfcMessageId is { } rid
            ? await (from r in db.OutboundRecipients.AsNoTracking()
                     join m in db.OutboundMessages.AsNoTracking() on r.OutboundMessageId equals m.Id
                     where m.RfcMessageId == rid
                     select new QueueItemDto(r.Id, r.Rcpt, r.Domain, r.Status.ToString().ToUpper(), r.Attempts, r.NextAttemptAt, r.LastResponse, r.LastMx, m.EnvelopeFrom,
                         m.RfcMessageId, m.CreatedAt)).Take(100).ToListAsync(ct)
            : [];
        return new TraceDetailDto(TraceHandler.Map(l), J(l.Details), stored, queue);
    }
}

// ---------- outbound queue ----------

public sealed record QueueItemDto(long Id, string Rcpt, string Domain, string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastResponse, string? LastMx,
    string EnvelopeFrom, string? MessageId, DateTimeOffset CreatedAt);

public sealed record ListQueueQuery(string? Status, string? Domain, int Page, int PageSize) : IRequest<Paged<QueueItemDto>>;

public sealed class ListQueueHandler(BlueMailDbContext db) : IRequestHandler<ListQueueQuery, Paged<QueueItemDto>>
{
    public async Task<Paged<QueueItemDto>> Handle(ListQueueQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var statuses = q.Status?.ToUpperInvariant() switch
        {
            "DEFERRED" => new[] { RecipientDeliveryStatus.Deferred },
            "QUEUED" => [RecipientDeliveryStatus.Queued],
            "BOUNCED" => [RecipientDeliveryStatus.Bounced, RecipientDeliveryStatus.Expired],
            "ALL" => Enum.GetValues<RecipientDeliveryStatus>(),
            _ => [RecipientDeliveryStatus.Queued, RecipientDeliveryStatus.Deferred],
        };
        var rows = from r in db.OutboundRecipients.AsNoTracking() where statuses.Contains(r.Status) select r;
        if (!string.IsNullOrWhiteSpace(q.Domain))
        {
            var d = q.Domain.Trim().ToLowerInvariant();
            rows = rows.Where(r => r.Domain == d);
        }

        // Order and page on the table, project last (EF cannot order by members of a constructed record).
        var total = await rows.CountAsync(ct);
        var items = await (from r in rows.OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
                           join m in db.OutboundMessages.AsNoTracking() on r.OutboundMessageId equals m.Id
                           select new QueueItemDto(r.Id, r.Rcpt, r.Domain, r.Status.ToString().ToUpper(), r.Attempts, r.NextAttemptAt, r.LastResponse, r.LastMx,
                               m.EnvelopeFrom, m.RfcMessageId, m.CreatedAt)).ToListAsync(ct);
        return new Paged<QueueItemDto>(items, total, page, size);
    }
}

/// <summary>"Retry now" moves the next attempt to now; "cancel" stops delivery (the sender gets no DSN: an administrator decided).</summary>
public sealed record QueueActionCommand(IReadOnlyList<long> Ids, bool Cancel) : IRequest<int>;

public sealed class QueueActionHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<QueueActionCommand, int>
{
    public async Task<int> Handle(QueueActionCommand c, CancellationToken ct)
    {
        var pending = new[] { RecipientDeliveryStatus.Queued, RecipientDeliveryStatus.Deferred };
        var q = db.OutboundRecipients.Where(r => c.Ids.Contains(r.Id) && pending.Contains(r.Status));
        var n = c.Cancel
            ? await q.ExecuteUpdateAsync(r => r.SetProperty(x => x.Status, RecipientDeliveryStatus.Cancelled).SetProperty(x => x.LastResponse, "cancelled by an administrator"), ct)
            : await q.ExecuteUpdateAsync(r => r.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow), ct);
        await audit.WriteAsync(new AuditEntry(c.Cancel ? "queue.cancel" : "queue.retry", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "queue",
            null, NewValueJson: JsonSerializer.Serialize(new { c.Ids, n })), ct);
        return n;
    }
}

// ---------- suppression list ----------

public sealed record SuppressionDto(long Id, string Address, string Reason, string? Detail, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

public sealed record ListSuppressionsQuery(string? Search, int Page, int PageSize) : IRequest<Paged<SuppressionDto>>;

public sealed class ListSuppressionsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListSuppressionsQuery, Paged<SuppressionDto>>
{
    public async Task<Paged<SuppressionDto>> Handle(ListSuppressionsQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Clamp(q.Page, q.PageSize);
        var query = db.Suppressions.AsNoTracking().Where(s => s.TenantId == me.TenantId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Address.Contains(s));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * size).Take(size)
            .Select(x => new SuppressionDto(x.Id, x.Address, x.Reason.ToString().ToUpper(), x.Detail, x.CreatedAt, x.ExpiresAt)).ToListAsync(ct);
        return new Paged<SuppressionDto>(rows, total, page, size);
    }
}

public sealed record RemoveSuppressionCommand(long Id) : IRequest<Unit>;

public sealed class RemoveSuppressionHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<RemoveSuppressionCommand, Unit>
{
    public async Task<Unit> Handle(RemoveSuppressionCommand c, CancellationToken ct)
    {
        var row = await db.Suppressions.FirstOrDefaultAsync(s => s.Id == c.Id && s.TenantId == me.TenantId, ct) ?? throw AppException.NotFound("not_found");
        db.Suppressions.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("suppression.remove", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "suppression", row.Address), ct);
        return Unit.Value;
    }
}

// ---------- sending health ----------

public sealed record DomainHealthDto(string Domain, int Delivered, int Deferred, int Bounced, double DeliveryRate);

public sealed record SendingHealthDto(int Days, int Delivered, int Bounced, int Complaints, double BounceRate, IReadOnlyList<DomainHealthDto> Domains,
    IReadOnlyList<IpListingDto> IpListings);

public sealed record IpListingDto(string Ip, string List, bool? Listed);

public sealed record SendingHealthQuery(int Days) : IRequest<SendingHealthDto>;

/// <summary>
/// Delivery per destination domain, bounce and complaint rates, and whether our sending IPs are on public block lists
/// (PROMPT 7.6). The IPs come from BM_OUTBOUND_IPS; IP warm-up appears with the IP pools of Phase 12.
/// </summary>
public sealed class SendingHealthHandler(BlueMailDbContext db, Dns.DnsClient dns, Microsoft.Extensions.Configuration.IConfiguration cfg) : IRequestHandler<SendingHealthQuery, SendingHealthDto>
{
    public static readonly string[] PublicLists = ["zen.spamhaus.org", "b.barracudacentral.org", "bl.spamcop.net"];

    public async Task<SendingHealthDto> Handle(SendingHealthQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 1, 90);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var rows = await (from r in db.OutboundRecipients.AsNoTracking()
                          join m in db.OutboundMessages.AsNoTracking() on r.OutboundMessageId equals m.Id
                          where m.CreatedAt >= since && !m.IsNotification
                          group r by r.Domain into g
                          select new
                          {
                              Domain = g.Key,
                              Delivered = g.Count(x => x.Status == RecipientDeliveryStatus.Delivered),
                              Deferred = g.Count(x => x.Status == RecipientDeliveryStatus.Deferred),
                              Bounced = g.Count(x => x.Status == RecipientDeliveryStatus.Bounced || x.Status == RecipientDeliveryStatus.Expired),
                          }).ToListAsync(ct);
        var complaints = await db.Suppressions.AsNoTracking().CountAsync(s => s.Reason == SuppressionReason.Complaint && s.CreatedAt >= since, ct);
        var delivered = rows.Sum(r => r.Delivered);
        var bounced = rows.Sum(r => r.Bounced);
        var domains = rows.OrderByDescending(r => r.Delivered + r.Deferred + r.Bounced).Take(50)
            .Select(r => new DomainHealthDto(r.Domain, r.Delivered, r.Deferred, r.Bounced, Rate(r.Delivered, r.Delivered + r.Bounced))).ToList();

        var listings = new List<IpListingDto>();
        foreach (var ip in (cfg["BM_OUTBOUND_IPS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(ip, out var addr))
            {
                continue;
            }

            foreach (var list in PublicLists)
            {
                bool? listed;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));
                    var (status, a) = await dns.LookupAddressesAsync(Antispam.DnsblChecker.ReverseName(addr) + "." + list, false, cts.Token);
                    listed = status switch
                    {
                        Dns.DnsStatus.Ok => a.Any(x => x.GetAddressBytes() is [127, var b, _, _] && b != 255) ? true : null,
                        Dns.DnsStatus.NxDomain or Dns.DnsStatus.NoData => false,
                        _ => null,
                    };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    listed = null;
                }

                listings.Add(new IpListingDto(ip, list, listed));
            }
        }

        return new SendingHealthDto(days, delivered, bounced, complaints, Rate(bounced, delivered + bounced), domains, listings);
    }

    private static double Rate(int part, int whole) => whole == 0 ? 0 : Math.Round(100.0 * part / whole, 2);
}
