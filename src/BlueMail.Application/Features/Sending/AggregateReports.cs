using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlueMail.Application.Features.Sending;

public sealed record ParsedAggregateRow(string Source, long Count, long Failed, string? Disposition, bool? DkimPass, bool? SpfPass, string? HeaderFrom, string? Detail);

public sealed record ParsedAggregateReport(AggregateReportKind Kind, string Domain, string OrgName, string ReportId, DateTimeOffset BeginAt, DateTimeOffset EndAt,
    IReadOnlyList<ParsedAggregateRow> Rows);

/// <summary>
/// Reads aggregate reports mailed to the platform's report addresses: DMARC (RFC 7489 appendix C; XML, usually gzip
/// or zip) and SMTP TLS reporting (RFC 8460 4.4; JSON, gzip). Untrusted input from anyone: DTDs are refused (no
/// external entities), and decompression stops at <see cref="MaxUncompressed"/> bytes (zip bombs).
/// </summary>
public static class AggregateReportParser
{
    public const int MaxUncompressed = 20 * 1024 * 1024;
    public const int MaxRows = 10_000;

    /// <summary>The reports in a message; empty when it carries none that can be read.</summary>
    public static IReadOnlyList<ParsedAggregateReport> Parse(byte[] raw)
    {
        MimeMessage msg;
        try
        {
            msg = MimeMessage.Parse(raw);
        }
        catch (InvalidDataException)
        {
            return [];
        }

        var source = new MemoryStream(raw, writable: false);
        var found = new List<ParsedAggregateReport>();
        foreach (var part in msg.Root.Walk().Where(p => p.IsLeaf))
        {
            var type = part.MediaType;
            var name = (part.FileName ?? "").ToLowerInvariant();
            if (type.StartsWith("text/plain", StringComparison.Ordinal) || type.StartsWith("text/html", StringComparison.Ordinal))
            {
                continue; // the human-readable cover note
            }

            byte[] body;
            try
            {
                body = part.ReadDecodedBody(source);
                foreach (var payload in Payloads(body, type, name))
                {
                    found.AddRange(ParsePayload(payload));
                }
            }
            catch (Exception e) when (e is InvalidDataException or XmlException or JsonException or FormatException or OverflowException
                                          or IOException or KeyNotFoundException or InvalidOperationException)
            {
                // A part we cannot read is skipped; the rest of the message may still hold a report.
            }
        }

        return found;
    }

    /// <summary>Decompressed contents of an attachment: gzip, zip (every entry) or the bytes as they are.</summary>
    private static IEnumerable<byte[]> Payloads(byte[] body, string type, string name)
    {
        if (body.Length >= 2 && body[0] == 0x1f && body[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
            yield return ReadCapped(gz);
        }
        else if (body.Length >= 4 && body[0] == (byte)'P' && body[1] == (byte)'K')
        {
            using var zip = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Take(10))
            {
                using var s = entry.Open();
                yield return ReadCapped(s);
            }
        }
        else if (type.Contains("xml", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal)
                 || name.EndsWith(".xml", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal))
        {
            yield return body;
        }
    }

    private static byte[] ReadCapped(Stream s)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > MaxUncompressed)
            {
                throw new InvalidDataException("report too large when decompressed");
            }

            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    public static IReadOnlyList<ParsedAggregateReport> ParsePayload(byte[] payload)
    {
        var i = 0;
        while (i < payload.Length && (char.IsWhiteSpace((char)payload[i]) || payload[i] is 0xEF or 0xBB or 0xBF))
        {
            i++;
        }

        return i < payload.Length && payload[i] == (byte)'{' ? ParseTlsRpt(payload) : [ParseDmarc(payload)];
    }

    /// <summary>A DMARC aggregate report (RFC 7489 appendix C; element names matched without namespace, so DMARCbis files read too).</summary>
    public static ParsedAggregateReport ParseDmarc(byte[] xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxUncompressed,
            IgnoreComments = true,
        };
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("empty report");
        if (root.Name.LocalName != "feedback")
        {
            throw new InvalidDataException("not a DMARC report");
        }

        var meta = Child(root, "report_metadata");
        var range = Child(meta, "date_range");
        var policy = Child(root, "policy_published");
        var rows = new List<ParsedAggregateRow>();
        foreach (var record in root.Elements().Where(e => e.Name.LocalName == "record").Take(MaxRows))
        {
            var row = Child(record, "row");
            var evaluated = Child(row, "policy_evaluated");
            rows.Add(new ParsedAggregateRow(
                Text(row, "source_ip"),
                long.Parse(Text(row, "count"), CultureInfo.InvariantCulture),
                0,
                Optional(evaluated, "disposition")?.ToLowerInvariant(),
                Optional(evaluated, "dkim") is { } dk ? dk.Equals("pass", StringComparison.OrdinalIgnoreCase) : null,
                Optional(evaluated, "spf") is { } sp ? sp.Equals("pass", StringComparison.OrdinalIgnoreCase) : null,
                record.Elements().FirstOrDefault(e => e.Name.LocalName == "identifiers") is { } ids ? Optional(ids, "header_from")?.ToLowerInvariant() : null,
                null));
        }

        return new ParsedAggregateReport(AggregateReportKind.Dmarc, Text(policy, "domain").ToLowerInvariant().TrimEnd('.'), Text(meta, "org_name"), Text(meta, "report_id"),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(Text(range, "begin"), CultureInfo.InvariantCulture)),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(Text(range, "end"), CultureInfo.InvariantCulture)), rows);
    }

    /// <summary>An SMTP TLS report (RFC 8460 4.4): one result per policy domain in it.</summary>
    public static IReadOnlyList<ParsedAggregateReport> ParseTlsRpt(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var r = doc.RootElement;
        var org = r.GetProperty("organization-name").GetString() ?? "";
        var id = r.GetProperty("report-id").GetString() ?? "";
        var range = r.GetProperty("date-range");
        var begin = DateTimeOffset.Parse(range.GetProperty("start-datetime").GetString()!, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(range.GetProperty("end-datetime").GetString()!, CultureInfo.InvariantCulture);
        var byDomain = new Dictionary<string, List<ParsedAggregateRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in r.GetProperty("policies").EnumerateArray().Take(MaxRows))
        {
            var policy = p.GetProperty("policy");
            var domain = (policy.GetProperty("policy-domain").GetString() ?? "").ToLowerInvariant().TrimEnd('.');
            var summary = p.GetProperty("summary");
            var failures = p.TryGetProperty("failure-details", out var fd) && fd.ValueKind == JsonValueKind.Array
                ? string.Join(", ", fd.EnumerateArray().Take(20).Select(f =>
                    (f.TryGetProperty("result-type", out var t) ? t.GetString() : "?") + ":" +
                    (f.TryGetProperty("failed-session-count", out var c) ? c.GetInt64().ToString(CultureInfo.InvariantCulture) : "?")))
                : null;
            if (!byDomain.TryGetValue(domain, out var rows))
            {
                byDomain[domain] = rows = [];
            }

            rows.Add(new ParsedAggregateRow(policy.GetProperty("policy-type").GetString() ?? "",
                summary.GetProperty("total-successful-session-count").GetInt64(), summary.GetProperty("total-failure-session-count").GetInt64(),
                null, null, null, null, string.IsNullOrEmpty(failures) ? null : failures[..Math.Min(failures.Length, 1000)]));
        }

        return byDomain.Select(kv => new ParsedAggregateReport(AggregateReportKind.TlsRpt, kv.Key, org, id, begin, end, kv.Value)).ToList();
    }

    private static XElement Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == name) ?? throw new InvalidDataException("missing " + name);

    private static string Text(XElement e, string name) => Child(e, name).Value.Trim();

    private static string? Optional(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
}

/// <summary>
/// Stores the aggregate reports of one message under the tenant that owns the reported domain. Reports about domains
/// nobody here owns are dropped; a report delivered twice is stored once.
/// </summary>
public sealed class AggregateReportProcessor(BlueMailDbContext db, ILogger<AggregateReportProcessor> log)
{
    public async Task<int> HandleAsync(byte[] raw, CancellationToken ct)
    {
        var stored = 0;
        foreach (var report in AggregateReportParser.Parse(raw))
        {
            var tenant = await db.Domains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Domain == report.Domain && d.DeletedAt == null).Select(d => (Guid?)d.TenantId).FirstOrDefaultAsync(ct);
            if (tenant is not { } tenantId)
            {
                log.LogInformation("{Kind} report from {Org} about {Domain}, which is not ours: dropped", report.Kind, report.OrgName, report.Domain);
                continue;
            }

            var exists = await db.AggregateReports.IgnoreQueryFilters().AnyAsync(r => r.Kind == report.Kind && r.OrgName == Cut(report.OrgName, 255)
                && r.ReportId == Cut(report.ReportId, 255) && r.Domain == report.Domain, ct);
            if (exists)
            {
                continue;
            }

            var row = new AggregateReport
            {
                TenantId = tenantId, Kind = report.Kind, Domain = report.Domain, OrgName = Cut(report.OrgName, 255), ReportId = Cut(report.ReportId, 255),
                BeginAt = report.BeginAt, EndAt = report.EndAt, ReceivedAt = DateTimeOffset.UtcNow,
            };
            db.AggregateReports.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // the same report arrived twice at once
                continue;
            }

            db.AggregateReportRows.AddRange(report.Rows.Select(x => new AggregateReportRow
            {
                TenantId = tenantId, ReportId = row.Id, Source = Cut(x.Source, 64), Count = x.Count, Failed = x.Failed, Disposition = x.Disposition,
                DkimPass = x.DkimPass, SpfPass = x.SpfPass, HeaderFrom = x.HeaderFrom is null ? null : Cut(x.HeaderFrom, 253), Detail = x.Detail,
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            stored++;
        }

        return stored;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
