using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlueMail.Dns;

namespace BlueMail.Auth;

public enum SpfResult { None, Neutral, Pass, Fail, SoftFail, TempError, PermError }

public sealed record SpfOutcome(SpfResult Result, string Domain, string? Explanation = null, string? Problem = null)
{
    public string AuthResultsValue => Result.ToString().ToLowerInvariant();
}

/// <summary>DNS answers needed by SPF. Production: <see cref="DnsClientSpfDns"/>; tests: zone tables.</summary>
public interface ISpfDns
{
    Task<(DnsStatus Status, IReadOnlyList<string> Records)> TxtAsync(string name, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<IPAddress> Addresses)> AddressesAsync(string name, AddressFamily family, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<string> Hosts)> MxAsync(string name, CancellationToken ct);
    Task<(DnsStatus Status, IReadOnlyList<string> Names)> PtrAsync(IPAddress address, CancellationToken ct);
}

public sealed class DnsClientSpfDns(DnsClient dns) : ISpfDns
{
    public async Task<(DnsStatus Status, IReadOnlyList<string> Records)> TxtAsync(string name, CancellationToken ct) => await dns.LookupTxtAsync(name, ct);

    public async Task<(DnsStatus Status, IReadOnlyList<IPAddress> Addresses)> AddressesAsync(string name, AddressFamily family, CancellationToken ct)
    {
        var r = await dns.ResolveAsync(name, family == AddressFamily.InterNetworkV6 ? DnsType.AAAA : DnsType.A, ct);
        return (r.Status, r.Data<IPAddress>().ToList());
    }

    public async Task<(DnsStatus Status, IReadOnlyList<string> Hosts)> MxAsync(string name, CancellationToken ct)
    {
        var (status, mx) = await dns.LookupMxAsync(name, ct);
        return (status, mx.Select(m => m.Exchange).ToList());
    }

    public Task<(DnsStatus Status, IReadOnlyList<string> Names)> PtrAsync(IPAddress address, CancellationToken ct) => dns.LookupPtrAsync(address, ct);
}

/// <summary>
/// SPF check_host() (RFC 7208): all mechanisms and modifiers, macros (section 7), the 10 DNS-mechanism limit and the
/// 2 void-lookup limit (4.6.4), explanation strings (6.2). Syntax errors anywhere in a record are permerror.
/// </summary>
public sealed class SpfEvaluator(ISpfDns dns)
{
    public const int MaxDnsMechanisms = 10;
    public const int MaxVoidLookups = 2;
    public const int MaxMxOrPtrNames = 10;

    public string ReceiverHostname { get; init; } = "unknown";

    /// <summary>Explanation used for a fail when the record has no usable exp= (RFC 7208 6.2: the receiver's default).</summary>
    public string? DefaultExplanation { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    private sealed class State
    {
        public int DnsMechanisms;
        public int VoidLookups;
        public required IPAddress Ip { get; init; }
        public required string Sender { get; init; }
        public required string Helo { get; init; }
        public string? ValidatedPtr;
        public CancellationToken Ct;
    }

    private sealed class SpfPermError(string message) : Exception(message);

    private sealed class SpfTempError(string message) : Exception(message);

    /// <summary>
    /// Evaluates SPF for an SMTP session. <paramref name="mailFrom"/> empty means the null reverse-path: the HELO
    /// identity is checked as postmaster@helo (RFC 7208 2.4).
    /// </summary>
    public async Task<SpfOutcome> CheckAsync(IPAddress ip, string mailFrom, string helo, CancellationToken ct = default)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var sender = string.IsNullOrEmpty(mailFrom) ? "postmaster@" + helo : mailFrom.Contains('@', StringComparison.Ordinal) ? mailFrom : "postmaster@" + mailFrom;
        var domain = sender[(sender.LastIndexOf('@') + 1)..];
        var state = new State { Ip = ip, Sender = sender, Helo = helo, Ct = ct };
        try
        {
            var (result, exp) = await CheckHostAsync(state, domain, top: true);
            return new SpfOutcome(result, domain, exp);
        }
        catch (SpfPermError e)
        {
            return new SpfOutcome(SpfResult.PermError, domain, Problem: e.Message);
        }
        catch (SpfTempError e)
        {
            return new SpfOutcome(SpfResult.TempError, domain, Problem: e.Message);
        }
    }

    private async Task<(SpfResult Result, string? Explanation)> CheckHostAsync(State st, string domain, bool top)
    {
        if (!ValidDomain(domain))
        {
            return (SpfResult.None, null);
        }

        var (status, txts) = await dns.TxtAsync(domain, st.Ct);
        if (status == DnsStatus.TempFail)
        {
            throw new SpfTempError("DNS error looking up " + domain);
        }

        var records = txts.Where(IsSpfRecord).ToList();
        if (records.Count == 0)
        {
            return (SpfResult.None, null);
        }

        if (records.Count > 1)
        {
            throw new SpfPermError("multiple SPF records for " + domain);
        }

        var terms = Parse(records[0]);
        foreach (var d in terms.Directives)
        {
            if (await MatchesAsync(st, d, domain))
            {
                if (d.Qualifier == SpfResult.Fail && top)
                {
                    return (SpfResult.Fail, await ExplanationAsync(st, terms.Exp, domain));
                }

                return (d.Qualifier, null);
            }
        }

        if (terms.Redirect is { } redirect)
        {
            CountDns(st);
            var target = await ExpandAsync(st, redirect, domain, forExp: false);
            if (!ValidDomain(target))
            {
                throw new SpfPermError("invalid redirect target");
            }

            var (r, exp) = await CheckHostAsync(st, target, top);
            if (r == SpfResult.None)
            {
                throw new SpfPermError("redirect target has no SPF record");
            }

            return (r, exp);
        }

        return (SpfResult.Neutral, null);
    }

    private static bool IsSpfRecord(string txt) =>
        txt.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) && (txt.Length == 6 || txt[6] == ' ');

    private sealed record Directive(SpfResult Qualifier, string Mechanism, string? DomainSpec, int? Cidr4, int? Cidr6, string? Ip);

    private sealed record Terms(List<Directive> Directives, string? Redirect, string? Exp);

    /// <summary>Parses a record; any syntax error is a permerror (RFC 7208 4.6).</summary>
    private static Terms Parse(string record)
    {
        var directives = new List<Directive>();
        string? redirect = null, exp = null;
        foreach (var term in record[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = term.IndexOf('=', StringComparison.Ordinal);
            var colon = term.IndexOf(':', StringComparison.Ordinal);
            var slash = term.IndexOf('/', StringComparison.Ordinal);
            if (eq > 0 && (colon < 0 || eq < colon) && (slash < 0 || eq < slash))
            {
                var name = term[..eq];
                var value = term[(eq + 1)..];
                if (!IsName(name))
                {
                    throw new SpfPermError("invalid modifier name");
                }

                ValidateMacroString(value, forExp: false);
                var known = name.Equals("redirect", StringComparison.OrdinalIgnoreCase) || name.Equals("exp", StringComparison.OrdinalIgnoreCase);
                if (known)
                {
                    if (value.Length == 0)
                    {
                        throw new SpfPermError(name + "= needs a domain");
                    }

                    ValidateDomainSpec(value);
                }

                if (name.Equals("redirect", StringComparison.OrdinalIgnoreCase))
                {
                    if (redirect is not null)
                    {
                        throw new SpfPermError("duplicate redirect");
                    }

                    redirect = value;
                }
                else if (name.Equals("exp", StringComparison.OrdinalIgnoreCase))
                {
                    if (exp is not null)
                    {
                        throw new SpfPermError("duplicate exp");
                    }

                    exp = value;
                }

                continue; // unknown modifiers are ignored (6)
            }

            var q = SpfResult.Pass;
            var t = term;
            if (t.Length > 0 && "+-~?".Contains(t[0]))
            {
                q = t[0] switch { '-' => SpfResult.Fail, '~' => SpfResult.SoftFail, '?' => SpfResult.Neutral, _ => SpfResult.Pass };
                t = t[1..];
            }

            var mechEnd = t.IndexOfAny([':', '/']);
            var mech = (mechEnd < 0 ? t : t[..mechEnd]).ToLowerInvariant();
            var rest = mechEnd < 0 ? "" : t[mechEnd..];
            switch (mech)
            {
                case "all":
                    if (rest.Length > 0)
                    {
                        throw new SpfPermError("all takes no argument");
                    }

                    directives.Add(new Directive(q, mech, null, null, null, null));
                    break;
                case "include" or "exists":
                    if (!rest.StartsWith(':') || rest.Length < 2)
                    {
                        throw new SpfPermError(mech + " needs a domain");
                    }

                    ValidateDomainSpec(rest[1..]);
                    directives.Add(new Directive(q, mech, rest[1..], null, null, null));
                    break;
                case "a" or "mx":
                    var (spec, c4, c6) = DomainAndCidr(rest);
                    directives.Add(new Directive(q, mech, spec, c4, c6, null));
                    break;
                case "ptr":
                    if (rest.Contains('/', StringComparison.Ordinal))
                    {
                        throw new SpfPermError("ptr takes no cidr");
                    }

                    string? ptrSpec = null;
                    if (rest.StartsWith(':'))
                    {
                        ptrSpec = rest[1..];
                        ValidateDomainSpec(ptrSpec);
                    }

                    directives.Add(new Directive(q, mech, ptrSpec, null, null, null));
                    break;
                case "ip4" or "ip6":
                    if (!rest.StartsWith(':'))
                    {
                        throw new SpfPermError(mech + " needs an address");
                    }

                    var ipText = rest[1..];
                    int? cidr = null;
                    var s = ipText.IndexOf('/', StringComparison.Ordinal);
                    if (s >= 0)
                    {
                        cidr = ParseCidr(ipText[(s + 1)..], mech == "ip4" ? 32 : 128);
                        ipText = ipText[..s];
                    }

                    if (!IPAddress.TryParse(ipText, out var addr) || (mech == "ip4") != (addr.AddressFamily == AddressFamily.InterNetwork)
                        || (mech == "ip4" && ipText.Count(c => c == '.') != 3))
                    {
                        throw new SpfPermError("invalid " + mech + " address");
                    }

                    directives.Add(new Directive(q, mech, null, mech == "ip4" ? cidr : null, mech == "ip6" ? cidr : null, ipText));
                    break;
                default:
                    throw new SpfPermError("unknown mechanism " + mech);
            }
        }

        return new Terms(directives, redirect, exp);
    }

    private static bool IsName(string n) => n.Length > 0 && char.IsAsciiLetter(n[0]) && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static int ParseCidr(string s, int max)
    {
        if (s.Length == 0 || s.Length > 3 || !s.All(char.IsAsciiDigit) || (s.Length > 1 && s[0] == '0'))
        {
            throw new SpfPermError("invalid cidr");
        }

        var v = int.Parse(s, CultureInfo.InvariantCulture);
        return v > max ? throw new SpfPermError("cidr too large") : v;
    }

    /// <summary>
    /// ":domain-spec" and dual-cidr-length. A '/' may appear inside the domain-spec ("a:foo:bar/baz.example.com"); the
    /// cidr is only a trailing "/digits", "//digits" or "/digits//digits".
    /// </summary>
    private static (string? Spec, int? Cidr4, int? Cidr6) DomainAndCidr(string rest)
    {
        int? c4 = null, c6 = null;
        var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(?<spec>(?::.*?)?)(?:/(?<c4>\d+))?(?://(?<c6>\d+))?$");
        if (!m.Success)
        {
            throw new SpfPermError("invalid mechanism argument");
        }

        if (m.Groups["c4"].Success)
        {
            c4 = ParseCidr(m.Groups["c4"].Value, 32);
        }

        if (m.Groups["c6"].Success)
        {
            c6 = ParseCidr(m.Groups["c6"].Value, 128);
        }

        var specPart = m.Groups["spec"].Value;
        if (specPart.Length == 0)
        {
            return (null, c4, c6);
        }

        var spec = specPart[1..];
        if (spec.Length == 0)
        {
            throw new SpfPermError("empty domain-spec");
        }

        ValidateDomainSpec(spec);
        return (spec, c4, c6);
    }

    /// <summary>domain-spec = macro-string domain-end (RFC 7208 7.1).</summary>
    private static void ValidateDomainSpec(string spec)
    {
        ValidateMacroString(spec, forExp: false);
        if (spec.Contains('%', StringComparison.Ordinal))
        {
            // The end of a macro-expanded spec is checked after expansion.
            var lastMacro = spec.LastIndexOf('}');
            if (lastMacro == spec.Length - 1)
            {
                return;
            }

            spec = spec[(lastMacro + 1)..];
            if (spec.Length == 0)
            {
                return;
            }
        }

        var trimmed = spec.TrimEnd('.');
        var tld = trimmed[(trimmed.LastIndexOf('.') + 1)..];
        if (!trimmed.Contains('.', StringComparison.Ordinal) || tld.Length == 0 || tld.All(char.IsAsciiDigit)
            || !tld.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || tld.StartsWith('-') || tld.EndsWith('-'))
        {
            throw new SpfPermError("invalid domain-spec " + spec);
        }
    }

    private static void ValidateMacroString(string s, bool forExp)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '%')
            {
                if (s[i] < 0x21 || s[i] > 0x7E)
                {
                    throw new SpfPermError("invalid character in macro-string");
                }

                continue;
            }

            if (i + 1 >= s.Length)
            {
                throw new SpfPermError("dangling %");
            }

            var n = s[i + 1];
            if (n is '%' or '_' or '-')
            {
                i++;
                continue;
            }

            if (n != '{')
            {
                throw new SpfPermError("invalid macro");
            }

            var close = s.IndexOf('}', i + 2);
            if (close < 0)
            {
                throw new SpfPermError("unterminated macro");
            }

            var body = s[(i + 2)..close];
            if (body.Length == 0 || !"slodiphcrtvSLODIPHCRTV".Contains(body[0]) || (!forExp && "crtCRT".Contains(body[0])))
            {
                throw new SpfPermError("invalid macro letter");
            }

            var j = 1;
            while (j < body.Length && char.IsAsciiDigit(body[j]))
            {
                j++;
            }

            if (j > 1 && int.Parse(body[1..j], CultureInfo.InvariantCulture) == 0)
            {
                throw new SpfPermError("zero transformer");
            }

            if (j < body.Length && body[j] is 'r' or 'R')
            {
                j++;
            }

            if (body[j..].Any(c => !".-+,/_=".Contains(c)))
            {
                throw new SpfPermError("invalid delimiter");
            }

            i = close;
        }
    }

    private async Task<bool> MatchesAsync(State st, Directive d, string domain)
    {
        switch (d.Mechanism)
        {
            case "all":
                return true;
            case "ip4" or "ip6":
                return InNetwork(st.Ip, IPAddress.Parse(d.Ip!), d.Mechanism == "ip4" ? d.Cidr4 ?? 32 : d.Cidr6 ?? 128,
                    d.Mechanism == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
            case "include":
            {
                CountDns(st);
                var target = await ExpandAsync(st, d.DomainSpec!, domain, false);
                var (r, _) = await CheckHostAsync(st, target, top: false);
                return r switch
                {
                    SpfResult.Pass => true,
                    SpfResult.Fail or SpfResult.SoftFail or SpfResult.Neutral => false,
                    SpfResult.TempError => throw new SpfTempError("include temperror"),
                    _ => throw new SpfPermError("include of " + target + " gave " + r),
                };
            }

            case "a":
            {
                CountDns(st);
                var target = d.DomainSpec is null ? domain : await ExpandAsync(st, d.DomainSpec, domain, false);
                return await HostMatchesAsync(st, target, d, countVoid: true);
            }

            case "mx":
            {
                CountDns(st);
                var target = d.DomainSpec is null ? domain : await ExpandAsync(st, d.DomainSpec, domain, false);
                var (status, hosts) = await dns.MxAsync(target, st.Ct);
                CheckStatus(st, status, countVoid: true);
                var real = hosts.Where(h => h != "." && h.Length > 0).ToList();
                if (real.Count > MaxMxOrPtrNames)
                {
                    throw new SpfPermError("too many MX names");
                }

                foreach (var h in real)
                {
                    if (await HostMatchesAsync(st, h.TrimEnd('.'), d, countVoid: false))
                    {
                        return true;
                    }
                }

                return false;
            }

            case "ptr":
            {
                CountDns(st);
                var target = d.DomainSpec is null ? domain : await ExpandAsync(st, d.DomainSpec, domain, false);
                foreach (var name in await ValidatedNamesAsync(st))
                {
                    if (name.Equals(target, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + target, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            case "exists":
            {
                CountDns(st);
                var target = await ExpandAsync(st, d.DomainSpec!, domain, false);
                var (status, addrs) = await dns.AddressesAsync(target, AddressFamily.InterNetwork, st.Ct);
                CheckStatus(st, status, countVoid: true);
                return addrs.Count > 0;
            }

            default:
                throw new SpfPermError("unknown mechanism");
        }
    }

    private async Task<bool> HostMatchesAsync(State st, string host, Directive d, bool countVoid)
    {
        if (!ValidDomain(host))
        {
            return false;
        }

        var family = st.Ip.AddressFamily;
        var (status, addrs) = await dns.AddressesAsync(host, family, st.Ct);
        CheckStatus(st, status, countVoid);
        var prefix = family == AddressFamily.InterNetwork ? d.Cidr4 ?? 32 : d.Cidr6 ?? 128;
        return addrs.Any(a => InNetwork(st.Ip, a, prefix, family));
    }

    /// <summary>PTR names of the client whose forward lookup contains the client address (5.5), at most 10.</summary>
    private async Task<List<string>> ValidatedNamesAsync(State st)
    {
        var (status, names) = await dns.PtrAsync(st.Ip, st.Ct);
        if (status == DnsStatus.TempFail)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var name in names.Take(MaxMxOrPtrNames))
        {
            var n = name.TrimEnd('.');
            var (s2, addrs) = await dns.AddressesAsync(n, st.Ip.AddressFamily, st.Ct);
            if (s2 == DnsStatus.Ok && addrs.Any(a => a.Equals(st.Ip)))
            {
                result.Add(n);
            }
        }

        return result;
    }

    private static void CheckStatus(State st, DnsStatus status, bool countVoid)
    {
        if (status == DnsStatus.TempFail)
        {
            throw new SpfTempError("DNS error");
        }

        if (countVoid && status is DnsStatus.NxDomain or DnsStatus.NoData && ++st.VoidLookups > MaxVoidLookups)
        {
            throw new SpfPermError("too many void lookups");
        }
    }

    private static void CountDns(State st)
    {
        if (++st.DnsMechanisms > MaxDnsMechanisms)
        {
            throw new SpfPermError("too many DNS lookups");
        }
    }

    private static bool InNetwork(IPAddress client, IPAddress network, int prefix, AddressFamily family)
    {
        if (client.AddressFamily != family || network.AddressFamily != family)
        {
            return false;
        }

        var a = client.GetAddressBytes();
        var b = network.GetAddressBytes();
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        var bits = prefix % 8;
        if (bits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - bits));
        return (a[full] & mask) == (b[full] & mask);
    }

    private async Task<string?> ExplanationAsync(State st, string? exp, string domain)
    {
        if (exp is null)
        {
            return DefaultExplanation;
        }

        try
        {
            var target = await ExpandAsync(st, exp, domain, false);
            var (status, txts) = await dns.TxtAsync(target, st.Ct);
            if (status != DnsStatus.Ok || txts.Count != 1)
            {
                return DefaultExplanation;
            }

            ValidateMacroString(txts[0].Replace(" ", "%_", StringComparison.Ordinal), forExp: true);
            return await ExpandAsync(st, txts[0], domain, forExp: true);
        }
        catch (Exception e) when (e is SpfPermError or SpfTempError)
        {
            return DefaultExplanation; // an invalid explanation is replaced by the default (6.2)
        }
    }

    /// <summary>Macro expansion (RFC 7208 7.3/7.4).</summary>
    private async Task<string> ExpandAsync(State st, string s, string domain, bool forExp)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }

            var n = s[++i];
            switch (n)
            {
                case '%': sb.Append('%'); continue;
                case '_': sb.Append(' '); continue;
                case '-': sb.Append("%20"); continue;
            }

            var close = s.IndexOf('}', i);
            var body = s[(i + 1)..close];
            i = close;
            var letter = body[0];
            var upper = char.IsUpper(letter);
            var value = await MacroValueAsync(st, char.ToLowerInvariant(letter), domain, forExp);
            var j = 1;
            while (j < body.Length && char.IsAsciiDigit(body[j]))
            {
                j++;
            }

            var keep = j > 1 ? int.Parse(body[1..j], CultureInfo.InvariantCulture) : int.MaxValue;
            var reverse = j < body.Length && body[j] is 'r' or 'R';
            var delims = body[(reverse ? j + 1 : j)..];
            var parts = value.Split(delims.Length == 0 ? ['.'] : delims.ToCharArray()).ToList();
            if (reverse)
            {
                parts.Reverse();
            }

            if (parts.Count > keep)
            {
                parts = parts.Skip(parts.Count - keep).ToList();
            }

            var expanded = string.Join('.', parts);
            sb.Append(upper ? Uri.EscapeDataString(expanded) : expanded);
        }

        var result = sb.ToString();
        if (!forExp && result.Length > 253)
        {
            // Left-truncate whole labels until the name fits (7.3).
            var labels = result.Split('.').ToList();
            while (string.Join('.', labels).Length > 253 && labels.Count > 1)
            {
                labels.RemoveAt(0);
            }

            result = string.Join('.', labels);
        }

        return result;
    }

    private async Task<string> MacroValueAsync(State st, char letter, string domain, bool forExp)
    {
        var at = st.Sender.LastIndexOf('@');
        return letter switch
        {
            's' => st.Sender,
            'l' => at > 0 ? st.Sender[..at] : "postmaster",
            'o' => st.Sender[(at + 1)..],
            'd' => domain,
            'i' => st.Ip.AddressFamily == AddressFamily.InterNetwork
                ? st.Ip.ToString()
                : string.Join('.', Convert.ToHexStringLower(st.Ip.GetAddressBytes()).ToCharArray()),
            'p' => st.ValidatedPtr ??= await PtrMacroAsync(st, domain),
            'v' => st.Ip.AddressFamily == AddressFamily.InterNetwork ? "in-addr" : "ip6",
            'h' => st.Helo,
            'c' when forExp => st.Ip.ToString(),
            'r' when forExp => ReceiverHostname,
            't' when forExp => Clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            _ => throw new SpfPermError("macro not allowed here"),
        };
    }

    private async Task<string> PtrMacroAsync(State st, string domain)
    {
        var names = await ValidatedNamesAsync(st);
        return names.FirstOrDefault(n => n.Equals(domain, StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault(n => n.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault()
               ?? "unknown";
    }

    private static bool ValidDomain(string domain)
    {
        var d = domain.TrimEnd('.');
        if (d.Length is 0 or > 253 || !d.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return d.Split('.').All(l => l.Length is > 0 and <= 63);
    }
}
