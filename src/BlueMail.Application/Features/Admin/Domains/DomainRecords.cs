using System.Globalization;
using BlueMail.Dns;
using BlueMail.Infrastructure.Configuration;

namespace BlueMail.Application.Features.Admin.Domains;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RecordKind>))]
public enum RecordKind { Verification, Mx, Spf, Dkim, Dmarc, MtaSts, MtaStsHost, TlsRpt, Autoconfig, Autodiscover, SrvSubmission, SrvImaps }

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RecordState>))]
public enum RecordState { Ok, Missing, Wrong, Unknown }

/// <summary>One DNS record the administrator must (or should) publish, and what DNS shows now.</summary>
public sealed record DnsRecordCheck(RecordKind Kind, string Type, string Name, string Expected, bool Required, RecordState State, IReadOnlyList<string> Found);

/// <summary>DNS lookups the wizard needs; the production one asks real resolvers, tests use a fake.</summary>
public interface IDomainDns
{
    Task<(DnsStatus Status, IReadOnlyList<string> Values)> TxtAsync(string name, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<string> Values)> MxAsync(string name, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<string> Values)> CnameAsync(string name, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<string> Values)> SrvAsync(string name, CancellationToken ct);
}

public sealed class ResolverDomainDns(DnsClient dns) : IDomainDns
{
    public Task<(DnsStatus Status, IReadOnlyList<string> Values)> TxtAsync(string name, CancellationToken ct) => dns.LookupTxtAsync(name, ct);

    public async Task<(DnsStatus Status, IReadOnlyList<string> Values)> MxAsync(string name, CancellationToken ct)
    {
        var (s, mx) = await dns.LookupMxAsync(name, ct);
        return (s, mx.Select(m => m.Exchange.TrimEnd('.').ToLowerInvariant()).ToList());
    }

    public async Task<(DnsStatus Status, IReadOnlyList<string> Values)> CnameAsync(string name, CancellationToken ct)
    {
        var r = await dns.QueryAsync(name, DnsType.CNAME, ct);
        return (r.Status, r.Records.Where(x => x.Type == DnsType.CNAME).Select(x => x.Data.ToString()!.TrimEnd('.').ToLowerInvariant()).ToList());
    }

    public async Task<(DnsStatus Status, IReadOnlyList<string> Values)> SrvAsync(string name, CancellationToken ct)
    {
        var r = await dns.ResolveAsync(name, DnsType.SRV, ct);
        return (r.Status, r.Data<SrvData>().Select(x => $"{x.Priority} {x.Weight} {x.Port} {x.Target.TrimEnd('.').ToLowerInvariant()}").ToList());
    }
}

/// <summary>
/// The records a domain needs (PROMPT 7.2): ownership TXT, MX, SPF, DKIM, DMARC (p=none with reports to BlueMail),
/// MTA-STS (+ policy host served by BlueMail), TLS-RPT, autoconfig/autodiscover and RFC 6186 SRV.
/// </summary>
public sealed class DomainRecordPlanner(BmSettings settings)
{
    public const string VerificationPrefix = "bluemail-verify=";

    /// <summary>The service's own mail domain: mx.example.com → example.com (hosts _spf, report mailboxes, mta-sts, autoconfig).</summary>
    public string PlatformMailDomain => settings.MailHostname.Contains('.', StringComparison.Ordinal)
        ? settings.MailHostname[(settings.MailHostname.IndexOf('.', StringComparison.Ordinal) + 1)..]
        : settings.MailHostname;

    public sealed record DkimPlan(string Selector, string PublicKey, string Status);

    public IReadOnlyList<(RecordKind Kind, string Type, string Name, string Expected, bool Required)> Plan(string domain, string token, IReadOnlyList<DkimPlan> dkim,
        DateTimeOffset policyId, bool verified = false)
    {
        var p = PlatformMailDomain;
        var list = new List<(RecordKind, string, string, string, bool)>
        {
            // The token is the whole TXT value ("bluemail-verify=…"); once verified the record may be removed.
            (RecordKind.Verification, "TXT", domain, token, !verified),
            (RecordKind.Mx, "MX", domain, "10 " + settings.MailHostname, true),
            (RecordKind.Spf, "TXT", domain, $"v=spf1 include:_spf.{p} -all", true),
        };
        list.AddRange(dkim.Select(k => (RecordKind.Dkim, "TXT", $"{k.Selector}._domainkey.{domain}", Sending.DkimKeyService.DnsRecord(k.PublicKey), k.Status != "RETIRING")));
        list.AddRange(
        [
            (RecordKind.Dmarc, "TXT", "_dmarc." + domain, $"v=DMARC1; p=none; rua=mailto:dmarc-reports@{p}", false),
            (RecordKind.MtaSts, "TXT", "_mta-sts." + domain, "v=STSv1; id=" + policyId.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), false),
            (RecordKind.MtaStsHost, "CNAME", "mta-sts." + domain, "mta-sts." + p, false),
            (RecordKind.TlsRpt, "TXT", "_smtp._tls." + domain, $"v=TLSRPTv1; rua=mailto:tls-reports@{p}", false),
            (RecordKind.Autoconfig, "CNAME", "autoconfig." + domain, "autoconfig." + p, false),
            (RecordKind.Autodiscover, "CNAME", "autodiscover." + domain, "autodiscover." + p, false),
            // RFC 6186 / 8314: implicit-TLS submission and IMAP on the platform's client host names.
            (RecordKind.SrvSubmission, "SRV", "_submissions._tcp." + domain, $"0 1 465 smtp.{p}", false),
            (RecordKind.SrvImaps, "SRV", "_imaps._tcp." + domain, $"0 1 993 imap.{p}", false),
        ]);
        // The platform's own domain: its host names are the targets themselves (A records), not CNAMEs to itself.
        return list.Where(r => !(r.Item2 == "CNAME" && string.Equals(r.Item3, r.Item4, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Asks DNS for every planned record and says, per record, whether what is there does the job.</summary>
    public async Task<IReadOnlyList<DnsRecordCheck>> CheckAsync(IDomainDns dns, string domain, string token, IReadOnlyList<DkimPlan> dkim, DateTimeOffset policyId,
        CancellationToken ct, bool verified = false)
    {
        var result = new List<DnsRecordCheck>();
        foreach (var (kind, type, name, expected, required) in Plan(domain, token, dkim, policyId, verified))
        {
            (DnsStatus Status, IReadOnlyList<string> Values) found;
            try
            {
                found = type switch
                {
                    "MX" => await dns.MxAsync(name, ct),
                    "CNAME" => await dns.CnameAsync(name, ct),
                    "SRV" => await dns.SrvAsync(name, ct),
                    _ => await dns.TxtAsync(name, ct),
                };
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                result.Add(new DnsRecordCheck(kind, type, name, expected, required, RecordState.Unknown, []));
                continue;
            }

            if (found.Status == DnsStatus.TempFail)
            {
                result.Add(new DnsRecordCheck(kind, type, name, expected, required, RecordState.Unknown, []));
                continue;
            }

            var values = found.Values;
            var relevant = kind switch
            {
                RecordKind.Spf => values.Where(v => v.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList(),
                RecordKind.Dmarc => values.Where(v => v.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList(),
                RecordKind.MtaSts => values.Where(v => v.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase)).ToList(),
                RecordKind.TlsRpt => values.Where(v => v.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase)).ToList(),
                RecordKind.Verification => values.Where(v => v.StartsWith(VerificationPrefix, StringComparison.Ordinal)).ToList(),
                RecordKind.Dkim => values.Where(v => v.Contains("p=", StringComparison.Ordinal)).ToList(),
                _ => values.ToList(),
            };
            var state = relevant.Count == 0 ? RecordState.Missing : Satisfies(kind, expected, relevant) ? RecordState.Ok : RecordState.Wrong;
            result.Add(new DnsRecordCheck(kind, type, name, expected, required, state, relevant));
        }

        return result;
    }

    /// <summary>What counts as right: exact where it must be, the meaningful part where admins may add more.</summary>
    public bool Satisfies(RecordKind kind, string expected, IReadOnlyList<string> found)
    {
        static string Norm(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.').ToLowerInvariant();
        switch (kind)
        {
            case RecordKind.Mx:
                // Any MX that points to our host (the preference is the admin's choice).
                return found.Count > 0 && found.All(f => f.Equals(settings.MailHostname, StringComparison.OrdinalIgnoreCase));
            case RecordKind.Spf:
                // Exactly one SPF record (RFC 7208 4.5) that authorises our include.
                return found.Count == 1 && found[0].Contains("include:_spf." + PlatformMailDomain, StringComparison.OrdinalIgnoreCase);
            case RecordKind.Dmarc:
            case RecordKind.MtaSts:
            case RecordKind.TlsRpt:
                return found.Count == 1; // a policy of the admin's choice is fine
            case RecordKind.Dkim:
                {
                    static string Key(string v) => v.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("p=", StringComparison.Ordinal))?[2..].Replace(" ", "", StringComparison.Ordinal) ?? "";
                    return found.Any(f => Key(f) == Key(expected));
                }

            case RecordKind.SrvSubmission:
            case RecordKind.SrvImaps:
                {
                    // Priority and weight are the admin's choice; port and target must match.
                    static string PortTarget(string v) => string.Join(' ', v.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)).TrimEnd('.').ToLowerInvariant();
                    return found.Any(f => PortTarget(f) == PortTarget(expected));
                }

            default:
                return found.Any(f => Norm(f) == Norm(expected));
        }
    }
}
