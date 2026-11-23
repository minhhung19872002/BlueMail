using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Dns;

namespace BlueMail.Dkim;

public enum ArcStatus { None, Pass, Fail }

public sealed record ArcResult(ArcStatus Status, int Instances, string Reason, string? OldestPassDomain = null)
{
    public string AuthResultsValue => Status.ToString().ToLowerInvariant();
}

/// <summary>
/// ARC chain validation (RFC 8617 5.2): sets 1..N each with exactly one ARC-Seal, ARC-Message-Signature and
/// ARC-Authentication-Results; cv=none on set 1 and cv=pass on the others; the newest AMS verifies like a DKIM
/// signature; every AS verifies over the ARC sets up to its own (relaxed header canonicalisation).
/// </summary>
public sealed class ArcVerifier(IDkimKeyResolver keys)
{
    public const int MaxInstances = 50;

    public async Task<ArcResult> VerifyAsync(Stream message, CancellationToken ct = default)
    {
        message.Position = 0;
        var headers = RawMessage.ReadHeaders(message);
        var bodyStart = message.Position;
        var sets = ArcSets.Collect(headers, out var structureError);
        if (structureError is not null)
        {
            return new ArcResult(ArcStatus.Fail, sets.Count, structureError);
        }

        if (sets.Count == 0)
        {
            return new ArcResult(ArcStatus.None, 0, "no ARC headers");
        }

        var n = sets.Count;
        for (var i = 1; i <= n; i++)
        {
            var cv = sets[i].SealTags.GetValueOrDefault("cv", "").ToLowerInvariant();
            var expected = i == 1 ? "none" : "pass";
            if (cv == "fail")
            {
                return new ArcResult(ArcStatus.Fail, n, $"cv=fail in instance {i}");
            }

            if (cv != expected)
            {
                return new ArcResult(ArcStatus.Fail, n, $"instance {i} has cv={cv}, expected {expected}");
            }
        }

        // Newest AMS: a DKIM signature without v=, keyed by its own d=/s=.
        var ams = sets[n];
        var amsOk = await VerifyAmsAsync(ams, headers, message, bodyStart, ct);
        if (amsOk is not null)
        {
            return new ArcResult(ArcStatus.Fail, n, "AMS " + n + ": " + amsOk);
        }

        string? oldestPass = null;
        for (var i = n; i >= 1; i--)
        {
            var err = await VerifySealAsync(sets, i, ct);
            if (err is not null)
            {
                return new ArcResult(ArcStatus.Fail, n, "AS " + i + ": " + err);
            }

            oldestPass = sets[i].SealTags.GetValueOrDefault("d");
        }

        return new ArcResult(ArcStatus.Pass, n, "pass", oldestPass);
    }

    private async Task<string?> VerifyAmsAsync(ArcSet set, List<RawHeader> headers, Stream message, long bodyStart, CancellationToken ct)
    {
        var tags = set.AmsTags;
        if (tags.GetValueOrDefault("a", "").ToLowerInvariant() != "rsa-sha256")
        {
            return "unsupported algorithm";
        }

        var sig = new DkimSignature { Header = set.Ams, Tags = tags };
        if (sig.SignedHeaders.Any(h => h.Equals("ARC-Seal", StringComparison.OrdinalIgnoreCase)))
        {
            return "AMS must not sign ARC-Seal";
        }

        var key = await LoadKeyAsync(sig.Selector, sig.Domain, ct);
        if (key.Error is not null)
        {
            return key.Error;
        }

        var (hc, bc) = sig.Canonicalization;
        message.Position = bodyStart;
        var bh = BodyHasher.Compute(message, bc, HashAlgorithmName.SHA256);
        if (!CryptographicOperations.FixedTimeEquals(bh, sig.BodyHash))
        {
            return "body hash mismatch";
        }

        var data = DkimSigner.SignedData(headers, sig.SignedHeaders, hc, sig.HeaderWithoutSignature());
        return key.Rsa!.VerifyData(data, sig.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) ? null : "signature mismatch";
    }

    private async Task<string?> VerifySealAsync(SortedDictionary<int, ArcSet> sets, int instance, CancellationToken ct)
    {
        var seal = sets[instance];
        var tags = seal.SealTags;
        if (tags.GetValueOrDefault("a", "").ToLowerInvariant() != "rsa-sha256")
        {
            return "unsupported algorithm";
        }

        if (tags.ContainsKey("h"))
        {
            return "ARC-Seal must not have h=";
        }

        var key = await LoadKeyAsync(tags.GetValueOrDefault("s", ""), tags.GetValueOrDefault("d", "").ToLowerInvariant(), ct);
        if (key.Error is not null)
        {
            return key.Error;
        }

        var data = ArcSets.SealData(sets, instance, seal.Seal.Raw);
        byte[] b;
        try
        {
            b = Convert.FromBase64String(TagList.StripWhitespace(tags.GetValueOrDefault("b", "")));
        }
        catch (FormatException)
        {
            return "invalid base64";
        }

        return key.Rsa!.VerifyData(data, b, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) ? null : "signature mismatch";
    }

    private async Task<(RSA? Rsa, string? Error)> LoadKeyAsync(string selector, string domain, CancellationToken ct)
    {
        var (status, records) = await keys.LookupAsync(selector, domain, ct);
        if (status != DnsStatus.Ok || records.Count != 1)
        {
            return (null, status == DnsStatus.TempFail ? "key lookup failed" : "no key record");
        }

        var key = DkimKey.Parse(records[0]);
        if (key?.Rsa is null || key.Revoked)
        {
            return (null, "unusable key");
        }

        return key.Rsa.KeySize < DkimVerifier.MinKeyBits ? (null, "key too short") : (key.Rsa, null);
    }
}

internal sealed record ArcSet(int Instance, RawHeader Seal, RawHeader Ams, RawHeader Aar, Dictionary<string, string> SealTags, Dictionary<string, string> AmsTags);

internal static class ArcSets
{
    public static SortedDictionary<int, ArcSet> Collect(List<RawHeader> headers, out string? error)
    {
        error = null;
        var seals = new Dictionary<int, List<RawHeader>>();
        var amss = new Dictionary<int, List<RawHeader>>();
        var aars = new Dictionary<int, List<RawHeader>>();
        foreach (var h in headers)
        {
            var target = h.Name.ToLowerInvariant() switch
            {
                "arc-seal" => seals,
                "arc-message-signature" => amss,
                "arc-authentication-results" => aars,
                _ => null,
            };
            if (target is null)
            {
                continue;
            }

            var i = Instance(h.Value);
            if (i is null or < 1 or > ArcVerifier.MaxInstances)
            {
                error = "invalid instance tag";
                return [];
            }

            if (!target.TryGetValue(i.Value, out var list))
            {
                target[i.Value] = list = [];
            }

            list.Add(h);
        }

        var result = new SortedDictionary<int, ArcSet>();
        var all = seals.Keys.Concat(amss.Keys).Concat(aars.Keys).Distinct().OrderBy(x => x).ToList();
        for (var k = 0; k < all.Count; k++)
        {
            var i = all[k];
            if (i != k + 1)
            {
                error = "instances are not contiguous from 1";
                return [];
            }

            if (!seals.TryGetValue(i, out var s) || s.Count != 1 || !amss.TryGetValue(i, out var m) || m.Count != 1 || !aars.TryGetValue(i, out var a) || a.Count != 1)
            {
                error = $"instance {i} does not have exactly one AS, AMS and AAR";
                return [];
            }

            var st = TagList.Parse(s[0].Value);
            var mt = TagList.Parse(m[0].Value);
            if (st is null || mt is null)
            {
                error = $"instance {i} has a malformed tag list";
                return [];
            }

            result[i] = new ArcSet(i, s[0], m[0], a[0], st, mt);
        }

        return result;
    }

    /// <summary>Value of the leading "i=N" tag (AAR has free text after the first ';').</summary>
    public static int? Instance(string value)
    {
        foreach (var part in value.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith("i=", StringComparison.Ordinal) && int.TryParse(p.AsSpan(2).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>RFC 8617 5.1.1: AAR, AMS, AS of every set 1..instance in order, relaxed; the last AS without b= and CRLF.</summary>
    public static byte[] SealData(SortedDictionary<int, ArcSet> sets, int instance, byte[] sealRaw)
    {
        using var ms = new MemoryStream();
        for (var j = 1; j <= instance; j++)
        {
            ms.Write(Canonicalizer.Header(sets[j].Aar, Canon.Relaxed));
            ms.Write(Canonicalizer.Header(sets[j].Ams, Canon.Relaxed));
            if (j < instance)
            {
                ms.Write(Canonicalizer.Header(sets[j].Seal, Canon.Relaxed));
            }
        }

        ms.Write(Canonicalizer.Header(DkimSignature.RemoveBValue(sealRaw), Canon.Relaxed, noCrlf: true));
        return ms.ToArray();
    }
}

/// <summary>Adds an ARC set when BlueMail forwards a message or expands a group to outside recipients (RFC 8617 5.1).</summary>
public sealed class ArcSigner
{
    private readonly RSA _key;

    public ArcSigner(string domain, string selector, RSA key)
    {
        if (key.KeySize < 2048)
        {
            throw new ArgumentException("ARC signing keys must be at least 2048 bits.", nameof(key));
        }

        Domain = domain.ToLowerInvariant();
        Selector = selector;
        _key = key;
    }

    public string Domain { get; }
    public string Selector { get; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns the three header lines (AS, AMS, AAR, each with CRLF) to prepend. <paramref name="authServId"/> and
    /// <paramref name="authResults"/> form the AAR ("authserv-id; spf=pass ..."). <paramref name="chainStatus"/> is
    /// the result of validating the existing chain (None when there is none).
    /// </summary>
    public byte[] Sign(Stream message, string authServId, string authResults, ArcStatus chainStatus)
    {
        message.Position = 0;
        var headers = RawMessage.ReadHeaders(message);
        var bodyStart = message.Position;
        var existing = ArcSets.Collect(headers, out var err);
        var i = err is null ? existing.Count + 1 : 1;
        if (err is null && existing.Count > 0 && existing[existing.Count].SealTags.GetValueOrDefault("cv") == "fail")
        {
            chainStatus = ArcStatus.Fail; // a failed chain stays failed; we seal it as such
        }

        var cv = i == 1 ? "none" : chainStatus == ArcStatus.Pass ? "pass" : "fail";
        var now = Clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var inst = i.ToString(CultureInfo.InvariantCulture);

        var aar = Encoding.UTF8.GetBytes($"ARC-Authentication-Results: i={inst}; {authServId}; {authResults}");

        message.Position = bodyStart;
        var bh = Convert.ToBase64String(BodyHasher.Compute(message, Canon.Relaxed, HashAlgorithmName.SHA256));
        var present = headers.Where(h => !h.Name.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase)).Select(h => h.Name.ToLowerInvariant()).ToHashSet();
        var signed = DkimSigner.DefaultHeaders.Where(present.Contains).Concat(present.Contains("dkim-signature") ? ["dkim-signature"] : []).ToList();
        var amsUnsigned = Encoding.ASCII.GetBytes($"ARC-Message-Signature: i={inst}; a=rsa-sha256; c=relaxed/relaxed; d={Domain}; s={Selector};\r\n\tt={now}; h={string.Join(':', signed)};\r\n\tbh={bh};\r\n\tb=");
        var amsData = DkimSigner.SignedData(headers, signed, Canon.Relaxed, amsUnsigned);
        var ams = amsUnsigned.Concat(Encoding.ASCII.GetBytes(DkimSigner.FoldBase64(Convert.ToBase64String(_key.SignData(amsData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))))).ToArray();

        var sets = err is null ? existing : [];
        var sealUnsigned = Encoding.ASCII.GetBytes($"ARC-Seal: i={inst}; a=rsa-sha256; t={now}; cv={cv};\r\n\td={Domain}; s={Selector};\r\n\tb=");
        sets[i] = new ArcSet(i, new RawHeader(sealUnsigned), new RawHeader(ams), new RawHeader(aar), [], []);
        var sealData = ArcSets.SealData(sets, i, sealUnsigned);
        var seal = sealUnsigned.Concat(Encoding.ASCII.GetBytes(DkimSigner.FoldBase64(Convert.ToBase64String(_key.SignData(sealData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))))).ToArray();

        return [.. seal, .. "\r\n"u8, .. ams, .. "\r\n"u8, .. aar, .. "\r\n"u8];
    }
}
