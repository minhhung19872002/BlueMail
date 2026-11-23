using System.Globalization;
using System.Text;
using BlueMail.Dkim;
using BlueMail.Dns;

namespace BlueMail.Auth;

public enum DmarcPolicy { None, Quarantine, Reject }

public enum DmarcResult { None, Pass, Fail, TempError, PermError }

public sealed record DmarcRecord(DmarcPolicy Policy, DmarcPolicy? SubdomainPolicy, int Percent, bool StrictDkim, bool StrictSpf,
    IReadOnlyList<string> Rua, IReadOnlyList<string> Ruf)
{
    /// <summary>Parses a "v=DMARC1; p=...; ..." record (RFC 7489 6.3). Null when it is not a valid DMARC record.</summary>
    public static DmarcRecord? Parse(string txt)
    {
        var tags = TagList.Parse(txt);
        if (tags is null || !tags.TryGetValue("v", out var v) || v != "DMARC1" || !txt.TrimStart().StartsWith("v=DMARC1", StringComparison.Ordinal))
        {
            return null;
        }

        static DmarcPolicy? Policy(string? s) => s?.ToLowerInvariant() switch
        {
            "none" => DmarcPolicy.None,
            "quarantine" => DmarcPolicy.Quarantine,
            "reject" => DmarcPolicy.Reject,
            _ => null,
        };

        var rua = Uris(tags.GetValueOrDefault("rua"));
        var p = Policy(tags.GetValueOrDefault("p"));
        if (p is null)
        {
            // 6.6.3: a record with an invalid or missing p= but a valid rua is treated as p=none.
            if (rua.Count == 0)
            {
                return null;
            }

            p = DmarcPolicy.None;
        }

        var pct = tags.TryGetValue("pct", out var pctText) && int.TryParse(pctText, NumberStyles.None, CultureInfo.InvariantCulture, out var pv) ? Math.Clamp(pv, 0, 100) : 100;
        return new DmarcRecord(p.Value, Policy(tags.GetValueOrDefault("sp")), pct,
            string.Equals(tags.GetValueOrDefault("adkim"), "s", StringComparison.OrdinalIgnoreCase),
            string.Equals(tags.GetValueOrDefault("aspf"), "s", StringComparison.OrdinalIgnoreCase),
            rua, Uris(tags.GetValueOrDefault("ruf")));
    }

    private static List<string> Uris(string? v) =>
        string.IsNullOrWhiteSpace(v) ? [] : v.Split(',').Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
}

public sealed record DmarcOutcome(DmarcResult Result, string FromDomain, DmarcPolicy Disposition, DmarcRecord? Record, string? PolicyDomain,
    bool SpfAligned, bool DkimAligned, string Reason)
{
    public string AuthResultsValue => Result.ToString().ToLowerInvariant();
}

/// <summary>
/// DMARC evaluation (RFC 7489): policy discovery at the From domain then its organizational domain (Public Suffix
/// List), identifier alignment (relaxed = same organizational domain, strict = equal), pct sampling, sp= for
/// subdomains. The SMTP layer decides what to do with <see cref="DmarcOutcome.Disposition"/>.
/// </summary>
public sealed class DmarcEvaluator(DnsClient dns, PublicSuffixList? psl = null)
{
    private readonly PublicSuffixList _psl = psl ?? PublicSuffixList.Default;

    public Func<int> Sampler { get; init; } = () => Random.Shared.Next(100);

    public async Task<DmarcOutcome> EvaluateAsync(string? fromDomain, SpfOutcome spf, string? mailFromDomain, IReadOnlyList<DkimResult> dkim, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fromDomain))
        {
            return new DmarcOutcome(DmarcResult.PermError, "", DmarcPolicy.None, null, null, false, false, "no single From domain");
        }

        var from = fromDomain.ToLowerInvariant().TrimEnd('.');
        var org = _psl.OrganizationalDomain(from) ?? from;
        var (lookup, record, policyDomain) = await DiscoverAsync(from, org, ct);
        if (lookup == DnsStatus.TempFail)
        {
            return new DmarcOutcome(DmarcResult.TempError, from, DmarcPolicy.None, null, null, false, false, "policy lookup failed");
        }

        if (record is null)
        {
            return new DmarcOutcome(DmarcResult.None, from, DmarcPolicy.None, null, null, false, false, "no policy");
        }

        var spfAligned = spf.Result == SpfResult.Pass && mailFromDomain is not null && Aligned(mailFromDomain, from, record.StrictSpf);
        var dkimAligned = dkim.Any(d => d.Status == DkimStatus.Pass && d.Domain is not null && Aligned(d.Domain, from, record.StrictDkim));
        if (spfAligned || dkimAligned)
        {
            return new DmarcOutcome(DmarcResult.Pass, from, DmarcPolicy.None, record, policyDomain, spfAligned, dkimAligned, "aligned");
        }

        // Subdomain policy applies when the policy came from the organizational domain for a different From domain.
        var policy = policyDomain != from && record.SubdomainPolicy is { } sp ? sp : record.Policy;
        if (record.Percent < 100 && Sampler() >= record.Percent)
        {
            policy = policy switch { DmarcPolicy.Reject => DmarcPolicy.Quarantine, DmarcPolicy.Quarantine => DmarcPolicy.None, _ => policy }; // 6.6.4
        }

        return new DmarcOutcome(DmarcResult.Fail, from, policy, record, policyDomain, false, false, "not aligned");
    }

    private bool Aligned(string identifier, string from, bool strict)
    {
        var id = identifier.ToLowerInvariant().TrimEnd('.');
        if (strict)
        {
            return id == from;
        }

        return string.Equals(_psl.OrganizationalDomain(id) ?? id, _psl.OrganizationalDomain(from) ?? from, StringComparison.Ordinal);
    }

    private async Task<(DnsStatus Status, DmarcRecord? Record, string? Domain)> DiscoverAsync(string from, string org, CancellationToken ct)
    {
        foreach (var domain in from == org ? new[] { from } : [from, org])
        {
            var (status, texts) = await dns.LookupTxtAsync("_dmarc." + domain, ct);
            if (status == DnsStatus.TempFail)
            {
                return (status, null, null);
            }

            var records = texts.Select(DmarcRecord.Parse).Where(r => r is not null).ToList();
            if (records.Count == 1)
            {
                return (DnsStatus.Ok, records[0], domain);
            }

            // Several valid records: as if none (6.6.3), keep looking at the organizational domain.
        }

        return (DnsStatus.NoData, null, null);
    }
}

/// <summary>Builds the Authentication-Results header (RFC 8601) added by BlueMail.Mx.</summary>
public static class AuthenticationResults
{
    public static string Build(string authServId, SpfOutcome spf, string? mailFrom, IReadOnlyList<DkimResult> dkim, DmarcOutcome dmarc, ArcResult arc)
    {
        var sb = new StringBuilder("Authentication-Results: ").Append(authServId);
        sb.Append(";\r\n\tspf=").Append(spf.AuthResultsValue).Append(" smtp.mailfrom=").Append(Value(string.IsNullOrEmpty(mailFrom) ? spf.Domain : mailFrom));
        if (dkim.Count == 0)
        {
            sb.Append(";\r\n\tdkim=none");
        }

        foreach (var d in dkim)
        {
            sb.Append(";\r\n\tdkim=").Append(d.AuthResultsValue);
            if (d.Status != DkimStatus.Pass)
            {
                sb.Append(" (").Append(Comment(d.Reason)).Append(')');
            }

            if (d.Domain is not null)
            {
                sb.Append(" header.d=").Append(Value(d.Domain));
            }

            if (d.Selector is not null)
            {
                sb.Append(" header.s=").Append(Value(d.Selector));
            }
        }

        sb.Append(";\r\n\tdmarc=").Append(dmarc.AuthResultsValue);
        if (dmarc.Record is { } r)
        {
            sb.Append(" (p=").Append(r.Policy.ToString().ToLowerInvariant()).Append(" dis=").Append(dmarc.Disposition.ToString().ToLowerInvariant()).Append(')');
        }

        if (dmarc.FromDomain.Length > 0)
        {
            sb.Append(" header.from=").Append(Value(dmarc.FromDomain));
        }

        sb.Append(";\r\n\tarc=").Append(arc.AuthResultsValue);
        return sb.Append("\r\n").ToString();
    }

    private static string Value(string v) => v.All(c => c > 32 && c < 127 && !"()<>@,;:\\\"/[]?=".Contains(c) || c is '@' or '.' or '-' or '_' or '+')
        ? v
        : "\"" + v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Comment(string s) => s.Replace("(", "[", StringComparison.Ordinal).Replace(")", "]", StringComparison.Ordinal);
}
