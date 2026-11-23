using System.Globalization;

namespace BlueMail.Auth;

/// <summary>
/// Public Suffix List (https://publicsuffix.org, MPL-2.0 data) for finding the organizational domain used by
/// DMARC alignment (RFC 7489 3.2). The list ships embedded and can be replaced at run time with a newer copy
/// downloaded by the worker (<see cref="Load(TextReader)"/>). Algorithm: publicsuffix.org "Formal Algorithm".
/// </summary>
public sealed class PublicSuffixList
{
    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wildcards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exceptions = new(StringComparer.Ordinal);
    private static readonly IdnMapping Idn = new();

    private static readonly Lazy<PublicSuffixList> Embedded = new(() =>
    {
        using var s = typeof(PublicSuffixList).Assembly.GetManifestResourceStream("public_suffix_list.dat")
                      ?? throw new InvalidOperationException("Embedded public_suffix_list.dat missing.");
        using var r = new StreamReader(s);
        return Load(r);
    });

    public static PublicSuffixList Default => Embedded.Value;

    public int RuleCount => _rules.Count + _wildcards.Count + _exceptions.Count;

    public static PublicSuffixList Load(TextReader reader)
    {
        var psl = new PublicSuffixList();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            rule = rule.Split(' ', '\t')[0];
            if (rule.StartsWith('!'))
            {
                psl._exceptions.Add(Ascii(rule[1..]));
            }
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                psl._wildcards.Add(Ascii(rule[2..]));
            }
            else
            {
                psl._rules.Add(Ascii(rule));
            }
        }

        return psl;
    }

    private static string Ascii(string s)
    {
        try
        {
            return Idn.GetAscii(s.ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            return s.ToLowerInvariant();
        }
    }

    /// <summary>The public suffix of <paramref name="domain"/> (e.g. "co.uk"), or null for invalid input.</summary>
    public string? PublicSuffix(string? domain)
    {
        var labels = Labels(domain);
        if (labels is null)
        {
            return null;
        }

        var n = SuffixLength(labels, out _);
        return string.Join('.', labels[^n..]);
    }

    /// <summary>
    /// Registrable domain: the public suffix plus one label ("bbc.co.uk"); unlisted TLDs use the default rule "*".
    /// Null when the domain is itself a public suffix or invalid.
    /// </summary>
    public string? RegistrableDomain(string? domain)
    {
        var labels = Labels(domain);
        if (labels is null)
        {
            return null;
        }

        var n = SuffixLength(labels, out _);
        return labels.Length <= n ? null : string.Join('.', labels[^(n + 1)..]);
    }

    /// <summary>DMARC organizational domain (RFC 7489 3.2) = the registrable domain.</summary>
    public string? OrganizationalDomain(string? domain) => RegistrableDomain(domain);

    private int SuffixLength(string[] labels, out bool listed)
    {
        // Longest matching rule; an exception rule wins and removes its leftmost label; default rule "*".
        var best = 1;
        listed = false;
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            var len = labels.Length - i;
            if (_exceptions.Contains(candidate))
            {
                listed = true;
                return len - 1;
            }

            if (_rules.Contains(candidate))
            {
                listed = true;
                best = Math.Max(best, len);
            }

            if (i + 1 < labels.Length && _wildcards.Contains(string.Join('.', labels[(i + 1)..])))
            {
                listed = true;
                best = Math.Max(best, len);
            }
        }

        return best;
    }

    private static string[]? Labels(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.StartsWith('.'))
        {
            return null;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(domain.Trim().TrimEnd('.').ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            return null;
        }

        var labels = ascii.Split('.');
        return labels.Any(l => l.Length == 0) ? null : labels;
    }
}
