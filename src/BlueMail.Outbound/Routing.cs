using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using BlueMail.Dns;

namespace BlueMail.Outbound;

public enum RouteError { None, NoSuchDomain, NullMx, Temporary }

public sealed record MxHost(string Host, ushort Preference);

public sealed record Route(RouteError Error, IReadOnlyList<MxHost> Hosts, string? Detail = null);

/// <summary>
/// Where mail for a domain goes (RFC 5321 5.1): MX records by preference (equal preferences in random order); no MX
/// records -> the domain itself (implicit MX); a single "." MX (RFC 7505) -> the domain accepts no mail, bounce now;
/// NXDOMAIN -> permanent; any DNS failure -> temporary (retry later, never bounce on a DNS hiccup).
/// </summary>
public sealed class MxResolver(DnsClient dns)
{
    public async Task<Route> ResolveAsync(string domain, CancellationToken ct)
    {
        var (status, mx) = await dns.LookupMxAsync(domain, ct);
        return Select(domain, status, mx);
    }

    public static Route Select(string domain, DnsStatus status, IReadOnlyList<MxData> mx)
    {
        switch (status)
        {
            case DnsStatus.NxDomain:
                return new Route(RouteError.NoSuchDomain, [], "domain does not exist");
            case DnsStatus.NoData:
                return new Route(RouteError.None, [new MxHost(domain, 0)]);
            case DnsStatus.Ok:
                break;
            default:
                return new Route(RouteError.Temporary, [], "DNS " + status);
        }

        if (mx.Count == 1 && (mx[0].Exchange is "." or "") )
        {
            return new Route(RouteError.NullMx, [], "domain accepts no mail (null MX, RFC 7505)");
        }

        var hosts = mx.Where(m => m.Exchange is not ("." or ""))
            .GroupBy(m => m.Preference)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => Random.Shared.Next()))
            .Select(m => new MxHost(m.Exchange.TrimEnd('.').ToLowerInvariant(), m.Preference))
            .ToList();
        return hosts.Count == 0 ? new Route(RouteError.NullMx, [], "no usable MX") : new Route(RouteError.None, hosts);
    }
}

public enum StsMode { None, Testing, Enforce }

public sealed record StsPolicy(StsMode Mode, IReadOnlyList<string> Mx, TimeSpan MaxAge, string Id)
{
    /// <summary>RFC 8461 4.1: "*.example.com" matches exactly one extra leftmost label.</summary>
    public bool Matches(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var p in Mx)
        {
            var pattern = p.TrimEnd('.').ToLowerInvariant();
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var dot = host.IndexOf('.');
                if (dot > 0 && host[(dot + 1)..] == pattern[2..])
                {
                    return true;
                }
            }
            else if (host == pattern)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the policy file (RFC 8461 3.2). Null when invalid (treated as "no policy").</summary>
    public static StsPolicy? Parse(string text, string id)
    {
        string? version = null;
        var mode = StsMode.None;
        var modeSeen = false;
        var mx = new List<string>();
        long maxAge = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "version": version = value; break;
                case "mode":
                    modeSeen = true;
                    mode = value switch { "enforce" => StsMode.Enforce, "testing" => StsMode.Testing, "none" => StsMode.None, _ => (StsMode)(-1) };
                    break;
                case "mx": mx.Add(value); break;
                case "max_age":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxAge))
                    {
                        maxAge = -1;
                    }

                    break;
            }
        }

        if (version != "STSv1" || !modeSeen || (int)mode < 0 || maxAge is < 0 or > 31557600 || (mode != StsMode.None && mx.Count == 0))
        {
            return null;
        }

        return new StsPolicy(mode, mx, TimeSpan.FromSeconds(maxAge), id);
    }
}

/// <summary>
/// MTA-STS (RFC 8461): TXT _mta-sts.domain gives the policy id; the policy is fetched over HTTPS with a validated
/// certificate from https://mta-sts.domain/.well-known/mta-sts.txt and cached for max_age. A cached policy stays in
/// force when the new fetch fails (RFC 8461 5.1), which is what protects against a downgrade attack.
/// </summary>
public sealed class MtaStsResolver(DnsClient dns, HttpClient http)
{
    private sealed record Cached(StsPolicy Policy, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<StsPolicy?> GetAsync(string domain, CancellationToken ct)
    {
        _cache.TryGetValue(domain, out var cached);
        var fresh = cached is not null && cached.FetchedAt + cached.Policy.MaxAge > DateTimeOffset.UtcNow;
        var (status, txts) = await dns.LookupTxtAsync("_mta-sts." + domain, ct);
        var id = status == DnsStatus.Ok
            ? txts.Select(ParseTxtId).FirstOrDefault(x => x is not null)
            : null;
        if (id is null)
        {
            return fresh ? cached!.Policy : null; // no (valid) record: keep a still-valid cached policy
        }

        if (fresh && cached!.Policy.Id == id)
        {
            return cached.Policy;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://mta-sts.{domain}/.well-known/mta-sts.txt");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentType?.MediaType == "text/plain" && resp.Content.Headers.ContentLength is null or <= 65536)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (StsPolicy.Parse(body.Length > 65536 ? body[..65536] : body, id) is { } policy)
                {
                    _cache[domain] = new Cached(policy, DateTimeOffset.UtcNow);
                    return policy;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Security.Authentication.AuthenticationException)
        {
            // fall through: keep the cached policy if any
        }

        return fresh ? cached!.Policy : null;
    }

    public static string? ParseTxtId(string txt)
    {
        var parts = txt.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "v=STSv1")
        {
            return null;
        }

        var id = parts.Skip(1).FirstOrDefault(p => p.StartsWith("id=", StringComparison.Ordinal))?[3..];
        return id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiLetterOrDigit) ? id : null;
    }
}

/// <summary>Retry schedule (PROMPT 2.4): 5 min, 15 min, 30 min, 1 h, 2 h, 4 h, then every 4 h until 5 days.</summary>
public static class RetrySchedule
{
    public static readonly TimeSpan[] Steps =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4)];

    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(5);
    public static readonly TimeSpan DelayNoticeAfter = TimeSpan.FromHours(4);

    public static TimeSpan After(int attempts) => Steps[Math.Clamp(attempts - 1, 0, Steps.Length - 1)];
}

/// <summary>Reading receivers' replies.</summary>
public static class Throttle
{
    /// <summary>The receiver asks us to slow down: 421, or a 4.7.x reply about rate or too many messages/connections.</summary>
    public static bool IsRateLimit(int code, string? enhanced, string text)
    {
        if (code == 421)
        {
            return true;
        }

        var aboutRate = text.Contains("rate", StringComparison.OrdinalIgnoreCase) || text.Contains("too many", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("slow down", StringComparison.OrdinalIgnoreCase);
        return code is >= 400 and < 500 && aboutRate && (enhanced is null || enhanced.StartsWith("4.7.", StringComparison.Ordinal) || enhanced.StartsWith("4.3.", StringComparison.Ordinal));
    }

    /// <summary>A hard "no such mailbox" (RFC 3463 5.1.1; 5.1.10 for null MX recipients): stop sending to it for a while.</summary>
    public static bool IsUnknownUser(int code, string? enhanced) => code >= 500 && enhanced is "5.1.1" or "5.1.10";
}
