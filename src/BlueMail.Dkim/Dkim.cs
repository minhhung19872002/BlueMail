using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Dns;

namespace BlueMail.Dkim;

/// <summary>RFC 6376 3.2 tag=value list. Tag names are case-sensitive; whitespace around tags and values is ignored.</summary>
public static class TagList
{
    public static Dictionary<string, string>? Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(';'))
        {
            var p = part.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return null;
            }

            var name = p[..eq].Trim();
            if (!result.TryAdd(name, p[(eq + 1)..].Trim()))
            {
                return null; // duplicate tag: invalid (3.2)
            }
        }

        return result;
    }

    public static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

public enum DkimStatus { Pass, Fail, Neutral, TempError, PermError, Policy }

/// <summary>Result of one DKIM-Signature (RFC 8601 "dkim=" values).</summary>
public sealed record DkimResult(DkimStatus Status, string? Domain, string? Selector, string? Identity, string Reason, int KeyBits = 0)
{
    public string AuthResultsValue => Status switch
    {
        DkimStatus.Pass => "pass",
        DkimStatus.Fail => "fail",
        DkimStatus.Neutral => "neutral",
        DkimStatus.TempError => "temperror",
        DkimStatus.Policy => "policy",
        _ => "permerror",
    };
}

/// <summary>Key record lookup (s._domainkey.d TXT). Implemented over BlueMail.Dns; tests may use an in-memory table.</summary>
public interface IDkimKeyResolver
{
    Task<(DnsStatus Status, IReadOnlyList<string> Records)> LookupAsync(string selector, string domain, CancellationToken ct);
}

public sealed class DnsDkimKeyResolver(DnsClient dns) : IDkimKeyResolver
{
    public Task<(DnsStatus Status, IReadOnlyList<string> Records)> LookupAsync(string selector, string domain, CancellationToken ct) =>
        dns.LookupTxtAsync($"{selector}._domainkey.{domain}", ct);
}

/// <summary>Parsed DKIM key record (RFC 6376 3.6.1).</summary>
public sealed record DkimKey(RSA? Rsa, string KeyType, bool Revoked, bool TestMode, bool StrictIdentity, IReadOnlyList<string>? HashAlgorithms, bool EmailService)
{
    public static DkimKey? Parse(string record)
    {
        var tags = TagList.Parse(record);
        if (tags is null || (tags.TryGetValue("v", out var v) && v != "DKIM1"))
        {
            return null;
        }

        var k = tags.TryGetValue("k", out var kt) ? kt.ToLowerInvariant() : "rsa";
        var p = tags.TryGetValue("p", out var pv) ? TagList.StripWhitespace(pv) : null;
        if (p is null)
        {
            return null;
        }

        var flags = tags.TryGetValue("t", out var t) ? t.Split(':').Select(f => f.Trim()).ToHashSet() : [];
        var hashes = tags.TryGetValue("h", out var h) ? h.Split(':').Select(x => x.Trim().ToLowerInvariant()).ToList() : null;
        var service = !tags.TryGetValue("s", out var s) || s.Split(':').Any(x => x.Trim() is "*" or "email");
        if (p.Length == 0)
        {
            return new DkimKey(null, k, true, flags.Contains("y"), flags.Contains("s"), hashes, service);
        }

        if (k != "rsa")
        {
            return new DkimKey(null, k, false, flags.Contains("y"), flags.Contains("s"), hashes, service);
        }

        try
        {
            var der = Convert.FromBase64String(p);
            var rsa = RSA.Create();
            try
            {
                rsa.ImportSubjectPublicKeyInfo(der, out _);
            }
            catch (CryptographicException)
            {
                rsa.ImportRSAPublicKey(der, out _); // some publishers use a bare PKCS#1 RSAPublicKey
            }

            return new DkimKey(rsa, k, false, flags.Contains("y"), flags.Contains("s"), hashes, service);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>A parsed DKIM-Signature (or ARC-Message-Signature) header.</summary>
public sealed class DkimSignature
{
    public required RawHeader Header { get; init; }
    public required Dictionary<string, string> Tags { get; init; }
    public string Algorithm => Tags.GetValueOrDefault("a", "").ToLowerInvariant();
    public string Domain => Tags.GetValueOrDefault("d", "").ToLowerInvariant();
    public string Selector => Tags.GetValueOrDefault("s", "");
    public byte[] Signature => Convert.FromBase64String(TagList.StripWhitespace(Tags.GetValueOrDefault("b", "")));
    public byte[] BodyHash => Convert.FromBase64String(TagList.StripWhitespace(Tags.GetValueOrDefault("bh", "")));
    public IReadOnlyList<string> SignedHeaders => Tags.GetValueOrDefault("h", "").Split(':').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    public string Identity => Tags.TryGetValue("i", out var i) ? i : "@" + Domain;

    public (Canon Header, Canon Body) Canonicalization
    {
        get
        {
            var c = Tags.GetValueOrDefault("c", "simple/simple").ToLowerInvariant().Split('/');
            static Canon Of(string s) => s == "relaxed" ? Canon.Relaxed : Canon.Simple;
            return (Of(c[0]), c.Length > 1 ? Of(c[1]) : Canon.Simple);
        }
    }

    /// <summary>The header with the value of b= removed (RFC 6376 3.7), for computing the signed data.</summary>
    public byte[] HeaderWithoutSignature() => RemoveBValue(Header.Raw);

    internal static byte[] RemoveBValue(byte[] raw)
    {
        // Find "b=" as a tag start (after ';' or the colon, ignoring whitespace), not "bh=".
        var s = Encoding.Latin1.GetString(raw);
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        var i = colon + 1;
        while (i < s.Length)
        {
            var j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j]))
            {
                j++;
            }

            if (j + 1 < s.Length && s[j] == 'b')
            {
                var k = j + 1;
                while (k < s.Length && char.IsWhiteSpace(s[k]))
                {
                    k++;
                }

                if (k < s.Length && s[k] == '=')
                {
                    var end = s.IndexOf(';', k);
                    var cut = s[..(k + 1)] + (end < 0 ? "" : s[end..]);
                    return Encoding.Latin1.GetBytes(cut);
                }
            }

            var next = s.IndexOf(';', i);
            if (next < 0)
            {
                break;
            }

            i = next + 1;
        }

        return raw;
    }
}

/// <summary>
/// DKIM signer (RFC 6376 5): rsa-sha256 with a 2048-bit (or larger) key, relaxed/relaxed by default.
/// Header fields that must not be added later (From, Subject, To, Date, ...) are over-signed: listed once more
/// than they occur, so an attacker appending another instance breaks the signature (8.15).
/// Ed25519 is not offered (PROMPT principle 8: no crypto outside the .NET BCL).
/// </summary>
public sealed class DkimSigner
{
    public static readonly string[] DefaultHeaders =
    [
        "from", "sender", "reply-to", "subject", "date", "message-id", "to", "cc", "in-reply-to", "references",
        "mime-version", "content-type", "content-transfer-encoding", "content-disposition", "list-id", "list-unsubscribe", "list-unsubscribe-post",
    ];

    public static readonly string[] OverSigned = ["from", "subject", "to", "date", "reply-to", "cc"];

    private readonly RSA _key;

    public DkimSigner(string domain, string selector, RSA key)
    {
        if (key.KeySize < 2048)
        {
            throw new ArgumentException("DKIM signing keys must be at least 2048 bits (RFC 8301 3.2 recommends 2048).", nameof(key));
        }

        Domain = domain.ToLowerInvariant();
        Selector = selector;
        _key = key;
    }

    public string Domain { get; }
    public string Selector { get; }
    public Canon HeaderCanon { get; init; } = Canon.Relaxed;
    public Canon BodyCanon { get; init; } = Canon.Relaxed;
    public TimeSpan? Expiry { get; init; } = TimeSpan.FromDays(7);
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Returns the complete "DKIM-Signature: ..." header line (with CRLF) to prepend to the message.</summary>
    public byte[] Sign(Stream message)
    {
        var headers = RawMessage.ReadHeaders(message);
        var bh = BodyHasher.Compute(message, BodyCanon, HashAlgorithmName.SHA256);
        var present = headers.GroupBy(h => h.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var h = new List<string>();
        foreach (var name in DefaultHeaders)
        {
            var count = present.GetValueOrDefault(name);
            var times = count + (OverSigned.Contains(name) ? 1 : 0);
            for (var i = 0; i < times; i++)
            {
                h.Add(name);
            }
        }

        var now = Clock().ToUnixTimeSeconds();
        var tags = new StringBuilder();
        tags.Append(CultureInfo.InvariantCulture, $"v=1; a=rsa-sha256; c={Name(HeaderCanon)}/{Name(BodyCanon)}; d={Domain}; s={Selector};\r\n\tt={now};");
        if (Expiry is { } x)
        {
            tags.Append(CultureInfo.InvariantCulture, $" x={now + (long)x.TotalSeconds};");
        }

        tags.Append("\r\n\th=").Append(Fold(string.Join(':', h))).Append(";\r\n\tbh=").Append(Convert.ToBase64String(bh)).Append(";\r\n\tb=");
        var unsigned = Encoding.ASCII.GetBytes("DKIM-Signature: " + tags);
        var data = SignedData(headers, h, HeaderCanon, unsigned);
        var sig = _key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return [.. unsigned, .. Encoding.ASCII.GetBytes(FoldBase64(Convert.ToBase64String(sig)) + "\r\n")];
    }

    internal static string Name(Canon c) => c == Canon.Relaxed ? "relaxed" : "simple";

    private static string Fold(string h)
    {
        var sb = new StringBuilder();
        var line = 0;
        foreach (var part in h.Split(':'))
        {
            if (sb.Length > 0)
            {
                sb.Append(':');
                line++;
                if (line > 60)
                {
                    sb.Append("\r\n\t ");
                    line = 0;
                }
            }

            sb.Append(part);
            line += part.Length;
        }

        return sb.ToString();
    }

    internal static string FoldBase64(string b64)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < b64.Length; i += 64)
        {
            if (i > 0)
            {
                sb.Append("\r\n\t ");
            }

            sb.Append(b64.AsSpan(i, Math.Min(64, b64.Length - i)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Data covered by the signature (RFC 6376 3.7): each name in h= takes the last not-yet-used instance
    /// (from the bottom); a name with no instance left contributes nothing. Then the signature header itself
    /// with empty b=, canonicalised, without its trailing CRLF.
    /// </summary>
    internal static byte[] SignedData(List<RawHeader> headers, IReadOnlyList<string> signedNames, Canon canon, byte[] signatureHeaderWithoutB)
    {
        var used = new HashSet<RawHeader>(ReferenceEqualityComparer.Instance);
        using var ms = new MemoryStream();
        foreach (var name in signedNames)
        {
            for (var i = headers.Count - 1; i >= 0; i--)
            {
                var h = headers[i];
                if (!used.Contains(h) && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    used.Add(h);
                    ms.Write(Canonicalizer.Header(h, canon));
                    break;
                }
            }
        }

        ms.Write(Canonicalizer.Header(signatureHeaderWithoutB, canon, noCrlf: true));
        return ms.ToArray();
    }
}

/// <summary>DKIM verifier (RFC 6376 6) with the restrictions of RFC 8301.</summary>
public sealed class DkimVerifier(IDkimKeyResolver keys)
{
    public const int MaxSignatures = 10;
    public const int MinKeyBits = 1024;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Verifies every DKIM-Signature of a message read from a seekable stream.</summary>
    public async Task<IReadOnlyList<DkimResult>> VerifyAsync(Stream message, CancellationToken ct = default)
    {
        message.Position = 0;
        var headers = RawMessage.ReadHeaders(message);
        var bodyStart = message.Position;
        var sigHeaders = headers.Where(h => h.Name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase)).Take(MaxSignatures).ToList();
        var results = new List<DkimResult>();
        var bodyHashes = new Dictionary<(Canon, string), byte[]>();
        foreach (var sh in sigHeaders)
        {
            results.Add(await VerifyOneAsync(sh, headers, message, bodyStart, bodyHashes, ct));
        }

        return results;
    }

    private async Task<DkimResult> VerifyOneAsync(RawHeader sh, List<RawHeader> headers, Stream message, long bodyStart,
        Dictionary<(Canon, string), byte[]> bodyHashes, CancellationToken ct)
    {
        var tags = TagList.Parse(sh.Value);
        if (tags is null)
        {
            return new DkimResult(DkimStatus.PermError, null, null, null, "malformed tag list");
        }

        var sig = new DkimSignature { Header = sh, Tags = tags };
        foreach (var required in new[] { "v", "a", "b", "bh", "d", "h", "s" })
        {
            if (!tags.ContainsKey(required))
            {
                return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, null, $"missing tag {required}=");
            }
        }

        if (tags["v"] != "1")
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, null, "unsupported version");
        }

        var alg = sig.Algorithm;
        if (alg == "rsa-sha1")
        {
            return new DkimResult(DkimStatus.Fail, sig.Domain, sig.Selector, sig.Identity, "rsa-sha1 is not accepted (RFC 8301 3.1)");
        }

        if (alg == "ed25519-sha256")
        {
            return new DkimResult(DkimStatus.Neutral, sig.Domain, sig.Selector, sig.Identity, "ed25519-sha256 not supported (no Ed25519 in the .NET BCL)");
        }

        if (alg != "rsa-sha256")
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, sig.Identity, "unknown algorithm " + alg);
        }

        if (!sig.SignedHeaders.Any(h => h.Equals("from", StringComparison.OrdinalIgnoreCase)))
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, sig.Identity, "From is not signed");
        }

        var identity = sig.Identity;
        var at = identity.LastIndexOf('@');
        var iDomain = at >= 0 ? identity[(at + 1)..].ToLowerInvariant() : "";
        if (!(iDomain == sig.Domain || iDomain.EndsWith("." + sig.Domain, StringComparison.Ordinal)))
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "i= is not within d=");
        }

        if (tags.TryGetValue("x", out var xs) && long.TryParse(xs, NumberStyles.None, CultureInfo.InvariantCulture, out var x))
        {
            if (tags.TryGetValue("t", out var ts) && long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && x < t)
            {
                return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "x= earlier than t=");
            }

            if (Clock().ToUnixTimeSeconds() > x)
            {
                return new DkimResult(DkimStatus.Fail, sig.Domain, sig.Selector, identity, "signature expired");
            }
        }

        byte[] expectedBh, signature;
        try
        {
            expectedBh = sig.BodyHash;
            signature = sig.Signature;
        }
        catch (FormatException)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "invalid base64");
        }

        var (lookupStatus, records) = await keys.LookupAsync(sig.Selector, sig.Domain, ct);
        if (lookupStatus == DnsStatus.TempFail)
        {
            return new DkimResult(DkimStatus.TempError, sig.Domain, sig.Selector, identity, "key lookup failed");
        }

        if (lookupStatus != DnsStatus.Ok || records.Count == 0)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "no key record");
        }

        var key = records.Count == 1 ? DkimKey.Parse(records[0]) : null;
        if (key is null)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, records.Count > 1 ? "several key records" : "invalid key record");
        }

        if (key.Revoked)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "key revoked");
        }

        if (key.KeyType != "rsa" || key.Rsa is null)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "key type mismatch");
        }

        if (key.HashAlgorithms is not null && !key.HashAlgorithms.Contains("sha256"))
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "key does not allow sha256");
        }

        if (!key.EmailService)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "key not for email");
        }

        if (key.StrictIdentity && iDomain != sig.Domain)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, "t=s forbids subdomain identity");
        }

        var bits = key.Rsa.KeySize;
        if (bits < MinKeyBits)
        {
            return new DkimResult(DkimStatus.PermError, sig.Domain, sig.Selector, identity, $"key too short ({bits} bits)", bits);
        }

        var (hc, bc) = sig.Canonicalization;
        // l= is ignored: the whole body is hashed, so content appended after a signed prefix breaks the signature.
        if (!bodyHashes.TryGetValue((bc, "sha256"), out var bh))
        {
            message.Position = bodyStart;
            bh = BodyHasher.Compute(message, bc, HashAlgorithmName.SHA256);
            bodyHashes[(bc, "sha256")] = bh;
        }

        if (!CryptographicOperations.FixedTimeEquals(bh, expectedBh))
        {
            return new DkimResult(DkimStatus.Fail, sig.Domain, sig.Selector, identity, "body hash mismatch", bits);
        }

        var data = DkimSigner.SignedData(headers, sig.SignedHeaders, hc, sig.HeaderWithoutSignature());
        var ok = key.Rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return ok
            ? new DkimResult(DkimStatus.Pass, sig.Domain, sig.Selector, identity, key.TestMode ? "pass (test mode key)" : "pass", bits)
            : new DkimResult(DkimStatus.Fail, sig.Domain, sig.Selector, identity, "signature mismatch", bits);
    }
}
