using System.Globalization;
using BlueMail.Application.Common;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Admin.Operations;

/// <summary>
/// A report (PROMPT 7.6): rows for the table and the chart, plus what was asked (filters) and whether the row limit
/// cut it short — both are printed in every export.
/// </summary>
public sealed record ReportDto(string Kind, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated,
    int Limit);

public sealed record ReportQuery(string Kind, DateTimeOffset? From, DateTimeOffset? To) : IRequest<ReportDto>;

public sealed class ReportHandler(BlueMailDbContext db) : IRequestHandler<ReportQuery, ReportDto>
{
    public const int Limit = 50_000;
    public const double SpamScore = 5.0;
    public static readonly string[] Kinds = ["MAIL_BY_DAY", "SPAM_RATE", "STORAGE_BY_USER", "USERS_WITHOUT_2FA", "DMARC_REPORTS", "TLS_REPORTS"];

    public async Task<ReportDto> Handle(ReportQuery q, CancellationToken ct)
    {
        var to = q.To ?? DateTimeOffset.UtcNow;
        var from = q.From ?? to.AddDays(-30);
        if (from >= to)
        {
            throw AppException.BadRequest("date_range_reversed");
        }

        var log = db.MailLog.AsNoTracking().Where(l => l.OccurredAt >= from && l.OccurredAt < to);
        switch (q.Kind)
        {
            case "MAIL_BY_DAY":
            {
                var rows = await log.GroupBy(l => l.OccurredAt.Date).OrderBy(g => g.Key).Take(Limit + 1).Select(g => new
                {
                    Day = g.Key,
                    Received = g.Count(l => l.Direction == MailDirection.Inbound && l.Status == MailLogStatus.Delivered),
                    Sent = g.Count(l => l.Direction == MailDirection.Outbound && l.Status == MailLogStatus.Delivered),
                    Internal = g.Count(l => l.Direction == MailDirection.Internal),
                    Rejected = g.Count(l => l.Status == MailLogStatus.Rejected),
                    Bounced = g.Count(l => l.Status == MailLogStatus.Bounced),
                }).ToListAsync(ct);
                return Build(q.Kind, from, to, ["day", "received", "sent", "internal", "rejected", "bounced"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Received, r.Sent, r.Internal, r.Rejected, r.Bounced]).ToList());
            }

            case "SPAM_RATE":
            {
                var rows = await log.Where(l => l.Direction == MailDirection.Inbound).GroupBy(l => l.OccurredAt.Date).OrderBy(g => g.Key).Take(Limit + 1).Select(g => new
                {
                    Day = g.Key,
                    Total = g.Count(),
                    Spam = g.Count(l => l.SpamScore >= SpamScore),
                    Rejected = g.Count(l => l.Status == MailLogStatus.Rejected),
                }).ToListAsync(ct);
                return Build(q.Kind, from, to, ["day", "inbound", "spam", "rejected", "spam_percent"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Total, r.Spam, r.Rejected,
                        r.Total == 0 ? 0.0 : Math.Round(100.0 * r.Spam / r.Total, 2)]).ToList());
            }

            case "STORAGE_BY_USER":
            {
                var rows = await (from u in db.Users.AsNoTracking()
                                  where u.DeletedAt == null
                                  let mb = db.Mailboxes.Where(m => m.OwnerId == u.Id).Select(m => m.Id).FirstOrDefault()
                                  select new
                                  {
                                      u.PrimaryEmail, u.QuotaBytes,
                                      Used = db.Messages.Where(m => m.MailboxId == mb && m.ExpungedAt == null && m.DeletedAt == null).Sum(m => (long?)m.Size) ?? 0,
                                  }).OrderByDescending(x => x.Used).Take(Limit + 1).ToListAsync(ct);
                return Build(q.Kind, from, to, ["user", "used_mb", "quota_mb", "used_percent"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.PrimaryEmail, Math.Round(r.Used / 1048576.0, 1), Math.Round(r.QuotaBytes / 1048576.0, 0),
                        r.QuotaBytes == 0 ? 0.0 : Math.Round(100.0 * r.Used / r.QuotaBytes, 1)]).ToList());
            }

            case "USERS_WITHOUT_2FA":
            {
                var rows = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null && u.Status == UserStatus.Active && !u.TwoFactorEnabled)
                    .OrderBy(u => u.PrimaryEmail).Take(Limit + 1).Select(u => new { u.PrimaryEmail, u.DisplayName, u.IsTenantAdmin, u.LastLoginAt }).ToListAsync(ct);
                return Build(q.Kind, from, to, ["user", "name", "admin", "last_login"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.PrimaryEmail, r.DisplayName, r.IsTenantAdmin ? "yes" : "no", r.LastLoginAt]).ToList());
            }

            case "DMARC_REPORTS":
            {
                // Aggregate reports received about our domains (PROMPT 7.6): who sent mail as the domain, and whether it passed.
                var rows = await (from r in db.AggregateReports.AsNoTracking()
                                  join x in db.AggregateReportRows.AsNoTracking() on r.Id equals x.ReportId
                                  where r.Kind == AggregateReportKind.Dmarc && r.BeginAt >= @from && r.BeginAt < to
                                  group x by new { Day = r.BeginAt.Date, r.Domain, r.OrgName, x.Source, x.Disposition, x.DkimPass, x.SpfPass } into g
                                  orderby g.Key.Day, g.Key.Domain, g.Key.OrgName, g.Key.Source
                                  select new { g.Key.Day, g.Key.Domain, g.Key.OrgName, g.Key.Source, Messages = g.Sum(x => x.Count), g.Key.DkimPass, g.Key.SpfPass, g.Key.Disposition })
                    .Take(Limit + 1).ToListAsync(ct);
                return Build(q.Kind, from, to, ["day", "domain", "reporter", "source_ip", "messages", "dkim", "spf", "disposition"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Domain, r.OrgName, r.Source, r.Messages,
                        PassFail(r.DkimPass), PassFail(r.SpfPass), r.Disposition]).ToList());
            }

            case "TLS_REPORTS":
            {
                var rows = await (from r in db.AggregateReports.AsNoTracking()
                                  join x in db.AggregateReportRows.AsNoTracking() on r.Id equals x.ReportId
                                  where r.Kind == AggregateReportKind.TlsRpt && r.BeginAt >= @from && r.BeginAt < to
                                  orderby r.BeginAt, r.Domain, r.OrgName
                                  select new { r.BeginAt, r.Domain, r.OrgName, x.Source, x.Count, x.Failed, x.Detail })
                    .Take(Limit + 1).ToListAsync(ct);
                return Build(q.Kind, from, to, ["day", "domain", "reporter", "policy", "successful", "failed", "failures"],
                    rows.Select(r => (IReadOnlyList<object?>)[r.BeginAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Domain, r.OrgName, r.Source,
                        r.Count, r.Failed, r.Detail]).ToList());
            }

            default:
                throw AppException.BadRequest("invalid_option");
        }
    }

    private static string? PassFail(bool? pass) => pass switch { true => "pass", false => "fail", null => null };

    private static ReportDto Build(string kind, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> columns, List<IReadOnlyList<object?>> rows) =>
        new(kind, from, to, columns, rows.Take(Limit).ToList(), rows.Count > Limit, Limit);
}

public sealed record ExportReportQuery(string Kind, DateTimeOffset? From, DateTimeOffset? To) : IRequest<(byte[] Content, string FileName)>;

/// <summary>XLSX with a header stating the report, the filters and whether the row limit was hit (PROMPT 7.6).</summary>
public sealed class ExportReportHandler(ISender mediator, ICurrentUser me, BlueMailDbContext db) : IRequestHandler<ExportReportQuery, (byte[] Content, string FileName)>
{
    public async Task<(byte[] Content, string FileName)> Handle(ExportReportQuery q, CancellationToken ct)
    {
        var r = await mediator.Send(new ReportQuery(q.Kind, q.From, q.To), ct);
        var locale = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => u.Locale).FirstOrDefaultAsync(ct) ?? "en";
        var c = CultureInfo.GetCultureInfo(locale);
        string T(string key, Dictionary<string, object?>? args = null) => JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key, c), c, args);
        var preamble = new List<string>
        {
            T("report.kind." + r.Kind),
            T("report.filter", new() { ["from"] = r.From.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), ["to"] = r.To.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) }),
            r.Truncated ? T("report.truncated", new() { ["limit"] = r.Limit }) : T("report.complete", new() { ["count"] = r.Rows.Count }),
        };
        var header = r.Columns.Select(col => T("report.col." + col)).ToList();
        return (Xlsx.Write(r.Kind, preamble, header, r.Rows), $"bluemail-{r.Kind.ToLowerInvariant()}-{DateTimeOffset.UtcNow:yyyyMMdd}.xlsx");
    }
}
