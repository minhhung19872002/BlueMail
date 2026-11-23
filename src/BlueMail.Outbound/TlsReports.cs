using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueMail.Application.Features.Rules;
using BlueMail.Dns;
using BlueMail.Domain.Tenants;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Outbound;

/// <summary>
/// SMTP TLS reporting (RFC 8460): once a day, for every recipient domain that publishes <c>_smtp._tls</c> with a rua,
/// the previous days' outcomes counted by the delivery worker are sent as a gzip JSON report - by mail (DKIM-signed,
/// RFC 8460 5.3) or HTTPS POST. Rows are marked reported either way, so a domain without rua is not looked up again.
/// </summary>
public sealed class TlsReportSender(IServiceScopeFactory scopes, DnsClient dns, IHttpClientFactory http, OutboundOptions options, ILogger<TlsReportSender> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var today = DateOnly.FromDateTime(options.Clock().UtcDateTime);
        var pending = await db.TlsResults.Where(r => r.ReportedAt == null && r.Day < today).ToListAsync(ct);
        var sent = 0;
        foreach (var batch in pending.GroupBy(r => (r.Day, r.PolicyDomain)))
        {
            var rua = await RuaAsync(batch.Key.PolicyDomain, ct);
            if (rua.Count > 0)
            {
                var (json, reportId) = Build(scope.ServiceProvider.GetRequiredService<BmSettings>(), batch.Key.Day, batch.Key.PolicyDomain, batch.ToList());
                foreach (var target in rua)
                {
                    try
                    {
                        await DeliverAsync(scope.ServiceProvider, db, target, batch.Key.PolicyDomain, batch.Key.Day, reportId, json, ct);
                        sent++;
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                    {
                        log.LogWarning(e, "TLS report for {Domain} to {Target} failed", batch.Key.PolicyDomain, target);
                    }
                }
            }

            foreach (var r in batch)
            {
                r.ReportedAt = options.Clock();
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>rua targets from "v=TLSRPTv1; rua=mailto:a@b,https://..." (RFC 8460 3).</summary>
    private async Task<List<string>> RuaAsync(string domain, CancellationToken ct)
    {
        var txt = await dns.ResolveAsync("_smtp._tls." + domain, DnsType.TXT, ct);
        var record = txt.Data<TxtData>().Select(t => t.Text).FirstOrDefault(t => t.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return [];
        }

        var rua = record.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));
        return rua is null
            ? []
            : rua[4..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
    }

    public static (string Json, string ReportId) Build(BmSettings settings, DateOnly day, string domain, List<Domain.Mail.TlsResult> rows)
    {
        var org = PlatformDomain(settings);
        var reportId = $"{day:yyyyMMdd}-{domain}@{org}";
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var policies = new JsonArray();
        foreach (var p in rows.GroupBy(r => r.PolicyType))
        {
            var failures = new JsonArray();
            foreach (var f in p.Where(r => r.FailureType is not null).GroupBy(r => (r.FailureType, r.SendingIp, r.MxHost, r.ReceivingIp)))
            {
                var d = new JsonObject { ["result-type"] = f.Key.FailureType, ["failed-session-count"] = f.Sum(r => r.Count) };
                if (f.Key.SendingIp.Length > 0)
                {
                    d["sending-mta-ip"] = f.Key.SendingIp;
                }

                if (f.Key.MxHost.Length > 0)
                {
                    d["receiving-mx-hostname"] = f.Key.MxHost;
                }

                if (f.Key.ReceivingIp.Length > 0)
                {
                    d["receiving-ip"] = f.Key.ReceivingIp;
                }

                failures.Add(d);
            }

            policies.Add(new JsonObject
            {
                ["policy"] = new JsonObject
                {
                    ["policy-type"] = p.Key,
                    ["policy-domain"] = domain,
                    ["mx-host"] = new JsonArray(p.Select(r => r.MxHost).Where(m => m.Length > 0).Distinct().Select(m => (JsonNode)m!).ToArray()),
                },
                ["summary"] = new JsonObject
                {
                    ["total-successful-session-count"] = p.Where(r => r.FailureType is null).Sum(r => r.Count),
                    ["total-failure-session-count"] = p.Where(r => r.FailureType is not null).Sum(r => r.Count),
                },
                ["failure-details"] = failures,
            });
        }

        var report = new JsonObject
        {
            ["organization-name"] = org,
            ["date-range"] = new JsonObject
            {
                ["start-datetime"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["end-datetime"] = start.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            },
            ["contact-info"] = "postmaster@" + org,
            ["report-id"] = reportId,
            ["policies"] = policies,
        };
        return (report.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), reportId);
    }

    private static string PlatformDomain(BmSettings s) => s.MailHostname[(s.MailHostname.IndexOf('.') + 1)..];

    private static byte[] Gzip(string json)
    {
        var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(System.Text.Encoding.UTF8.GetBytes(json));
        }

        return buffer.ToArray();
    }

    private async Task DeliverAsync(IServiceProvider sp, BlueMailDbContext db, string target, string domain, DateOnly day, string reportId, string json, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<BmSettings>();
        var org = PlatformDomain(settings);
        var gz = Gzip(json);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var fileName = $"{org}!{domain}!{start}!{start + 86399}!{Guid.NewGuid():N}.json.gz"; // RFC 8460 5.3
        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var client = http.CreateClient("tlsrpt");
            using var content = new ByteArrayContent(gz);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/tlsrpt+gzip");
            using var response = await client.PostAsync(new Uri(target), content, ct);
            response.EnsureSuccessStatusCode();
            return;
        }

        var to = Uri.UnescapeDataString(target[7..]);
        var tenant = await db.Domains.IgnoreQueryFilters().Where(d => d.Domain == org && d.DeletedAt == null).Select(d => (Guid?)d.TenantId).FirstOrDefaultAsync(ct)
                     ?? await db.Tenants.IgnoreQueryFilters().Where(t => t.Kind == TenantKind.Platform && t.DeletedAt == null).Select(t => t.Id).FirstAsync(ct);
        var from = "tlsrpt@" + org;
        var message = new MessageBuilder
        {
            From = new MailboxAddress(null, from),
            To = [new MailboxAddress(null, to)],
            Subject = $"Report Domain: {domain} Submitter: {org} Report-ID: <{reportId}>",
            Date = options.Clock(),
            MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            TextBody = $"This is an aggregate TLS report from {org} (RFC 8460).",
            Attachments = [new BuilderAttachment { FileName = fileName, ContentType = "application/tlsrpt+gzip", Open = () => new MemoryStream(gz, writable: false) }],
            ExtraHeaders = [("TLS-Report-Domain", domain), ("TLS-Report-Submitter", org)],
        }.ToBytes();
        var mail = sp.GetRequiredService<SystemMail>();
        var signed = await mail.SignAsync(tenant, org, message, ct);
        await mail.SendAsync(tenant, null, "", to, signed, notification: true, null, null, ct);
    }
}
