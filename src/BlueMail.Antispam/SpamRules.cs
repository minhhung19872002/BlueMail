using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace BlueMail.Antispam;

/// <summary>Everything the scorer looks at for one message (filled by BlueMail.Mx; no I/O in the scorer).</summary>
public sealed record SpamInput
{
    public required IPAddress RemoteIp { get; init; }
    public string? Helo { get; init; }
    public string? ReverseDns { get; init; }         // forward-confirmed reverse DNS name, null when missing or not confirmed
    public string Spf { get; init; } = "none";       // pass | fail | softfail | neutral | none | temperror | permerror
    public IReadOnlyList<(string Result, string? Domain)> Dkim { get; init; } = [];
    public string Dmarc { get; init; } = "none";     // pass | fail | none | temperror | permerror
    public string DmarcPolicy { get; init; } = "none";
    public string Arc { get; init; } = "none";
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? Date { get; init; }
    public bool HasMessageId { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public int Images { get; init; }
    public IReadOnlyList<(string Href, string Text)> Links { get; init; } = [];
    public IReadOnlyList<string> SuspiciousAttachmentCodes { get; init; } = [];
    public IReadOnlyList<string> OurDomains { get; init; } = [];
    public string OurHostname { get; init; } = "";
    public bool SenderKnown { get; init; }            // the recipient wrote to this sender before (reputation)
    public bool SenderDomainFirstSeen { get; init; }  // the tenant never received mail from this domain
    public IReadOnlyList<(string Source, double Score)> ListedIn { get; init; } = [];
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One rule that fired: stable code (translated as "spam.rule.&lt;code&gt;"), points and an optional detail.</summary>
public sealed record SpamHit(string Code, double Score, string? Detail = null);

public sealed record SpamVerdict(double Score, IReadOnlyList<SpamHit> Hits, double? BayesProbability)
{
    public bool IsSpam(double threshold) => Score >= threshold;
}

/// <summary>
/// Weighted rules (PROMPT 2.3). Scores are deliberately conservative: a single authentication failure never makes
/// a message spam on its own, because false positives hurt more than missed spam (target &lt; 0.1 %). Negative
/// points reward evidence of legitimacy (aligned DKIM, DMARC pass, known correspondent).
/// </summary>
public static partial class SpamRules
{
    private static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "ymail.com", "icloud.com", "me.com", "aol.com",
        "gmx.com", "gmx.de", "mail.ru", "yandex.ru", "yandex.com", "qq.com", "163.com", "126.com", "naver.com", "proton.me", "protonmail.com",
    };

    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "t.ly",
    };

    // Phrases of credential phishing in several languages (lower case, accents removed for Latin scripts).
    private static readonly string[] Phishing =
    [
        "verify your account", "confirm your password", "your account will be suspended", "your password expires", "unusual sign-in activity",
        "update your payment", "your mailbox is full", "click here to keep your password",
        "xac minh tai khoan", "mat khau sap het han", "tai khoan cua ban se bi khoa", "cap nhat thong tin thanh toan", "hop thu cua ban da day",
        "verifizieren sie ihr konto", "verifique su cuenta", "verifiez votre compte", "подтвердите вашу учетную запись", "账户将被冻结", "验证您的账户",
    ];

    /// <summary>Advance-fee / prize fraud: one phrase is ordinary, several together are the classic scam (scored by count).</summary>
    private static readonly string[] AdvanceFee =
    [
        "you have won", "you won", "lottery", "claim your prize", "claim your winnings", "western union", "moneygram", "processing fee",
        "bank account details", "unclaimed inheritance", "next of kin", "beneficiary of", "million dollars", "$1,000,000",
        "trung thuong", "nhan giai thuong", "phi xu ly", "xo so", "thong tin tai khoan ngan hang",
    ];

    public static SpamVerdict Score(SpamInput i, BayesResult? bayes)
    {
        var hits = new List<SpamHit>();
        void Hit(string code, double score, string? detail = null) => hits.Add(new SpamHit(code, score, detail));

        // --- authentication (results computed by BlueMail.Auth / BlueMail.Dkim at reception) ---
        switch (i.Spf)
        {
            case "fail": Hit("SPF_FAIL", 2.5); break;
            case "softfail": Hit("SPF_SOFTFAIL", 1.0); break;
            case "none": Hit("SPF_NONE", 0.3); break;
            case "pass": Hit("SPF_PASS", -0.3); break;
        }

        var fromDomain = DomainOf(i.FromAddress);
        if (i.Dkim.Count == 0)
        {
            Hit("DKIM_NONE", 0.5);
        }
        else if (i.Dkim.All(d => d.Result != "pass"))
        {
            Hit("DKIM_INVALID", 1.5);
        }
        else if (!i.SenderDomainFirstSeen && fromDomain is not null
                 && i.Dkim.Any(d => d.Result == "pass" && d.Domain is { } dd && (dd == fromDomain || fromDomain.EndsWith("." + dd, StringComparison.Ordinal))))
        {
            // Authentication proves where mail comes from, not that it is wanted: a domain seen for the first time
            // (spammers register their own and sign correctly) earns no discount.
            Hit("DKIM_VALID_ALIGNED", -0.5);
        }

        if (i.Dmarc == "fail")
        {
            Hit(i.DmarcPolicy == "quarantine" ? "DMARC_FAIL_QUARANTINE" : "DMARC_FAIL", i.DmarcPolicy == "quarantine" ? 5.0 : 2.5);
        }
        else if (i.Dmarc == "pass" && !i.SenderDomainFirstSeen)
        {
            Hit("DMARC_PASS", -0.5);
        }

        if (i.Arc == "fail")
        {
            Hit("ARC_FAIL", 0.5);
        }

        // --- connection ---
        if (i.ReverseDns is null)
        {
            Hit("RDNS_NONE", 0.8);
        }

        var helo = (i.Helo ?? "").Trim().ToLowerInvariant();
        if (helo.StartsWith('[') && helo.EndsWith(']'))
        {
            var lit = helo[1..^1].Replace("ipv6:", "", StringComparison.Ordinal);
            if (!IPAddress.TryParse(lit, out var ip) || !ip.Equals(i.RemoteIp))
            {
                Hit("HELO_IP_MISMATCH", 1.5, helo);
            }
        }
        else if (helo.Length > 0 && !helo.Contains('.', StringComparison.Ordinal))
        {
            Hit("HELO_NO_DOT", 1.0, helo);
        }
        else if (helo == i.OurHostname.ToLowerInvariant() || i.OurDomains.Any(d => helo == d.ToLowerInvariant()))
        {
            Hit("HELO_FORGED_OURS", 3.0, helo);
        }

        foreach (var (source, score) in i.ListedIn)
        {
            Hit("LISTED", score, source);
        }

        // --- headers ---
        if (i.Date is null)
        {
            Hit("HDR_MISSING_DATE", 1.0);
        }
        else if (i.Date > i.Now.AddHours(24))
        {
            Hit("HDR_DATE_FUTURE", 1.5);
        }
        else if (i.Date < i.Now.AddDays(-60))
        {
            Hit("HDR_DATE_OLD", 0.5);
        }

        if (!i.HasMessageId)
        {
            Hit("HDR_MISSING_MSGID", 1.0);
        }

        if (i.FromName is { Length: > 0 } name && fromDomain is not null
            && (EmailOrDomain().Match(name) is { Success: true } m ? m.Value : i.OurDomains.FirstOrDefault(d => name.Contains(d, StringComparison.OrdinalIgnoreCase))) is { } named)
        {
            // Our own domains count whatever their TLD (a display name "bluemail.test IT" from elsewhere is a spoof).
            var shown = DomainOf(named.Contains('@', StringComparison.Ordinal) ? named : "x@" + named);
            if (shown is not null && shown != fromDomain && !fromDomain.EndsWith("." + shown, StringComparison.Ordinal))
            {
                Hit("FROM_NAME_SPOOFS_DOMAIN", 2.5, shown);
            }
        }

        var replyDomain = DomainOf(i.ReplyTo);
        if (replyDomain is not null && fromDomain is not null && replyDomain != fromDomain && FreeMail.Contains(replyDomain) && !FreeMail.Contains(fromDomain))
        {
            Hit("REPLYTO_FREEMAIL", 1.5, replyDomain);
        }

        if (fromDomain is not null && i.OurDomains.Count > 0 && LooksLike(fromDomain, i.OurDomains) is { } imitated)
        {
            Hit("FROM_LOOKALIKE_DOMAIN", 3.5, imitated);
        }

        if (fromDomain is not null && fromDomain.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal)) && MixedScripts(ToUnicode(fromDomain)))
        {
            Hit("FROM_MIXED_SCRIPT_DOMAIN", 2.5, fromDomain);
        }

        // --- subject and body ---
        var subject = i.Subject ?? "";
        var letters = subject.Where(char.IsLetter).ToList();
        if (letters.Count >= 12 && letters.All(c => !char.IsLower(c)) && letters.Any(char.IsUpper))
        {
            Hit("SUBJ_ALL_CAPS", 0.5);
        }

        if (subject.Count(c => c == '!') >= 3)
        {
            Hit("SUBJ_EXCLAIM", 0.5);
        }

        var text = (i.TextBody ?? "").Trim();
        if (i.HtmlBody is { Length: > 0 } && text.Length < 20 && i.Images > 0)
        {
            Hit("HTML_IMAGE_ONLY", 1.5);
        }

        var lowered = Tokenizer.RemoveMarks((subject + " " + text).ToLowerInvariant());
        if (Phishing.FirstOrDefault(p => lowered.Contains(Tokenizer.RemoveMarks(p), StringComparison.Ordinal)) is { } phrase)
        {
            Hit("PHISHING_PHRASE", 1.5, phrase);
        }

        var fee = AdvanceFee.Where(p => lowered.Contains(p, StringComparison.Ordinal)).ToList();
        if (fee.Count >= 2)
        {
            Hit("ADVANCE_FEE", Math.Min(5.0, 3.0 + (fee.Count - 2)), string.Join(", ", fee.Take(4)));
        }

        // --- links ---
        var deceptive = i.Links.Count(l => Deceptive(l.Text, l.Href));
        if (deceptive > 0)
        {
            Hit("LINK_TEXT_MISMATCH", 2.5, deceptive.ToString(CultureInfo.InvariantCulture));
        }

        var hosts = i.Links.Select(l => Uri.TryCreate(l.Href, UriKind.Absolute, out var u) ? u : null).Where(u => u is not null && u.Scheme is "http" or "https")
            .Select(u => u!.Host.ToLowerInvariant()).ToList();
        if (hosts.Any(h => IPAddress.TryParse(h.Trim('[', ']'), out _)))
        {
            Hit("URL_IP_LITERAL", 2.0);
        }

        if (hosts.Any(Shorteners.Contains))
        {
            Hit("URL_SHORTENER", 0.5);
        }

        if (hosts.Any(h => h.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal))))
        {
            Hit("URL_PUNYCODE", 1.0);
        }

        // --- attachments flagged SUSPICIOUS by BlueMail.Scanner (MALICIOUS ones never get here) ---
        foreach (var code in i.SuspiciousAttachmentCodes.Distinct())
        {
            Hit("ATTACHMENT_SUSPICIOUS", 3.0, code);
        }

        // --- reputation ---
        if (i.SenderKnown)
        {
            Hit("SENDER_KNOWN", -3.0);
        }
        else if (i.SenderDomainFirstSeen)
        {
            Hit("SENDER_DOMAIN_NEW", 0.5);
        }

        // --- Bayes ---
        if (bayes is { } b)
        {
            var (code, pts) = b.Probability switch
            {
                >= 0.999 => ("BAYES_999", 5.0),
                >= 0.99 => ("BAYES_99", 4.0),
                >= 0.95 => ("BAYES_95", 3.0),
                >= 0.80 => ("BAYES_80", 1.5),
                <= 0.01 => ("BAYES_00", -2.0),
                <= 0.05 => ("BAYES_05", -1.0),
                _ => ((string?)null, 0.0),
            };
            if (code is not null)
            {
                Hit(code, pts, b.Probability.ToString("0.000", CultureInfo.InvariantCulture));
            }
        }

        return new SpamVerdict(Math.Round(hits.Sum(h => h.Score), 2), hits, bayes?.Probability);
    }

    private static string ToUnicode(string domain)
    {
        try
        {
            return new IdnMapping().GetUnicode(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }

    public static string? DomainOf(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? null : address[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
    }

    /// <summary>Visible link text that names a host different from the target host.</summary>
    public static bool Deceptive(string text, string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var m = HostInText().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        var shown = m.Groups["host"].Value.ToLowerInvariant();
        var real = target.Host.ToLowerInvariant();
        return !(real == shown || real.EndsWith("." + shown, StringComparison.Ordinal) || shown.EndsWith("." + real, StringComparison.Ordinal));
    }

    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x', ['у'] = 'y', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ο'] = 'o', ['α'] = 'a',
        ['0'] = 'o', ['1'] = 'l',
    };

    public static string Skeleton(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => Confusables.GetValueOrDefault(c, c)).ToArray();
        return new string(chars).Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal).Replace('i', 'l');
    }

    /// <summary>A sender domain imitating one of ours: same skeleton or one edit away (two for long names), but not ours.</summary>
    public static string? LooksLike(string domain, IReadOnlyList<string> ours)
    {
        foreach (var o in ours.Select(x => x.ToLowerInvariant()))
        {
            if (domain == o || domain.EndsWith("." + o, StringComparison.Ordinal))
            {
                return null;
            }
        }

        foreach (var o in ours.Select(x => x.ToLowerInvariant()))
        {
            if (Skeleton(domain) == Skeleton(o) || (o.Length >= 6 && Distance(domain, o) <= (o.Length >= 10 ? 2 : 1)))
            {
                return o;
            }

            // Cousin domains: our name (or its look-alike) inside another label, e.g. "bluemaiI-support.test".
            var brand = Skeleton(o.Split('.')[0]);
            if (brand.Length >= 5 && domain.Split('.').SkipLast(1).Any(l => Skeleton(l) != brand && Skeleton(l).Contains(brand, StringComparison.Ordinal)))
            {
                return o;
            }
        }

        return null;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var x = 0; x <= a.Length; x++)
        {
            d[x, 0] = x;
        }

        for (var y = 0; y <= b.Length; y++)
        {
            d[0, y] = y;
        }

        for (var x = 1; x <= a.Length; x++)
        {
            for (var y = 1; y <= b.Length; y++)
            {
                var cost = a[x - 1] == b[y - 1] ? 0 : 1;
                d[x, y] = Math.Min(Math.Min(d[x - 1, y] + 1, d[x, y - 1] + 1), d[x - 1, y - 1] + cost);
                if (x > 1 && y > 1 && a[x - 1] == b[y - 2] && a[x - 2] == b[y - 1])
                {
                    d[x, y] = Math.Min(d[x, y], d[x - 2, y - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>Letters from more than one script in a single label (e.g. Latin + Cyrillic), the classic homograph attack.</summary>
    public static bool MixedScripts(string domain)
    {
        foreach (var label in domain.Split('.'))
        {
            var scripts = label.Where(char.IsLetter).Select(ScriptOf).Distinct().Count();
            if (scripts > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static int ScriptOf(char c) => c switch
    {
        < 'ʰ' => 0,                         // Latin (incl. extended)
        >= 'Ͱ' and <= 'Ͽ' => 1,              // Greek
        >= 'Ѐ' and <= 'ӿ' => 2,              // Cyrillic
        >= '֐' and <= '׿' => 3,    // Hebrew
        >= '؀' and <= 'ۿ' => 4,    // Arabic
        >= 'Ḁ' and <= 'ỿ' => 0,    // Latin Extended Additional (Vietnamese)
        _ => 9,
    };

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+|\b([a-z0-9-]+\.)+(com|net|org|vn|io|co|uk|de|fr|jp|cn|ru|info|biz|bank|gov)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailOrDomain();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?<host>(?:[\p{L}\p{N}-]+\.)+[\p{L}]{2,63})(?:[/:?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostInText();
}

