using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueMail.Dns;

/// <summary>What the TLSA records of an MX host ask for (RFC 7672 2.2).</summary>
public enum DaneState
{
    /// <summary>No DNSSEC-secure TLSA records: deliver as without DANE (opportunistic or MTA-STS).</summary>
    None,

    /// <summary>Usable secure TLSA records: TLS is required and the certificate must match one of them.</summary>
    Required,

    /// <summary>The TLSA lookup was bogus or failed: do not deliver to this host now (RFC 7672 2.2.1).</summary>
    Unavailable,
}

public sealed record DanePolicy(DaneState State, IReadOnlyList<TlsaData> Records, string? Reason = null);

/// <summary>
/// DANE for SMTP (RFC 7672, RFC 7671). Only DANE-TA(2) and DANE-EE(3) are usable for SMTP; PKIX-TA(0)/PKIX-EE(1)
/// records are ignored (RFC 7672 3.1.3). DANE-EE ignores names and dates (RFC 7672 3.1.1); DANE-TA requires the
/// certificate to name the MX host (RFC 7672 3.2.2).
/// </summary>
public static class Dane
{
    /// <summary>TLSA policy of one MX host. <paramref name="mxSecure"/>: the MX RRset itself validated (RFC 7672 2.2.1).</summary>
    public static async Task<DanePolicy> PolicyAsync(DnssecValidator dnssec, string mxHost, int port, bool mxSecure, CancellationToken ct)
    {
        if (!mxSecure)
        {
            return new DanePolicy(DaneState.None, [], "MX not DNSSEC-secure");
        }

        // The host's address records must be secure too, otherwise TLSA is not looked up (RFC 7672 2.2.2).
        var address = await dnssec.ResolveAsync(mxHost, DnsType.A, ct);
        if (address.Status != DnssecStatus.Secure)
        {
            return address.Status == DnssecStatus.Bogus
                ? new DanePolicy(DaneState.Unavailable, [], "address of " + mxHost + " is bogus: " + address.Reason)
                : new DanePolicy(DaneState.None, [], "address of " + mxHost + " not secure");
        }

        var tlsa = await dnssec.ResolveAsync($"_{port}._tcp.{mxHost.TrimEnd('.')}", DnsType.TLSA, ct);
        switch (tlsa.Status)
        {
            case DnssecStatus.Bogus or DnssecStatus.Indeterminate:
                return new DanePolicy(DaneState.Unavailable, [], "TLSA of " + mxHost + ": " + tlsa.Reason);
            case DnssecStatus.Insecure:
                return new DanePolicy(DaneState.None, [], "TLSA zone insecure");
        }

        var records = tlsa.Records.Select(r => r.Data).OfType<TlsaData>().ToList();
        var usable = records.Where(Usable).ToList();
        if (usable.Count == 0)
        {
            // RFC 7672 2.2: records present but none usable -> TLS still mandatory, no authentication.
            return records.Count == 0 ? new DanePolicy(DaneState.None, []) : new DanePolicy(DaneState.Required, [], "no usable TLSA record");
        }

        return new DanePolicy(DaneState.Required, usable);
    }

    public static bool Usable(TlsaData t) => t.Usage is 2 or 3 && t.Selector is 0 or 1 && t.MatchingType is 0 or 1 or 2;

    /// <summary>
    /// Whether the presented certificate (and the chain the server sent) satisfies the policy. With required TLS but no
    /// usable record, any certificate is accepted (encryption without authentication, RFC 7672 2.2).
    /// </summary>
    public static bool Matches(DanePolicy policy, string mxHost, X509Certificate2 leaf, IReadOnlyList<X509Certificate2> chain)
    {
        if (policy.Records.Count == 0)
        {
            return true;
        }

        foreach (var t in policy.Records)
        {
            if (t.Usage == 3 && Match(t, leaf))
            {
                return true; // DANE-EE: the key or certificate itself, no name or date checks
            }

            if (t.Usage == 2 && leaf.MatchesHostname(mxHost.TrimEnd('.')) && ChainsTo(leaf, chain, t))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ChainsTo(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> chain, TlsaData anchor)
    {
        using var builder = new X509Chain();
        builder.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        builder.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var c in chain)
        {
            if (Match(anchor, c))
            {
                builder.ChainPolicy.CustomTrustStore.Add(c);
            }
            else
            {
                builder.ChainPolicy.ExtraStore.Add(c);
            }
        }

        if (builder.ChainPolicy.CustomTrustStore.Count == 0)
        {
            return false;
        }

        // A trust anchor given as an intermediate is fine: accept a partial chain ending at it.
        builder.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        return builder.Build(leaf) || builder.ChainElements.Any(e => Match(anchor, e.Certificate));
    }

    /// <summary>RFC 6698 2.1: selector 0 = whole certificate, 1 = SubjectPublicKeyInfo; matching 0 = exact, 1 = SHA-256, 2 = SHA-512.</summary>
    public static bool Match(TlsaData t, X509Certificate2 cert)
    {
        var data = t.Selector == 0 ? cert.RawData : cert.PublicKey.ExportSubjectPublicKeyInfo();
        var value = t.MatchingType switch
        {
            0 => data,
            1 => SHA256.HashData(data),
            2 => SHA512.HashData(data),
            _ => [],
        };
        return value.Length > 0 && CryptographicOperations.FixedTimeEquals(value, t.Certificate);
    }
}
