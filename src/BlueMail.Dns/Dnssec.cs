using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BlueMail.Dns;

public enum DnssecStatus { Secure, Insecure, Bogus, Indeterminate }

/// <summary>Answer of a validated lookup: the records (when any), the response code and how far they can be trusted.</summary>
public sealed record DnssecResult(DnssecStatus Status, DnsRCode RCode, IReadOnlyList<DnsRecord> Records, string? Reason = null);

/// <summary>
/// DNSSEC validation done here, not trusted from the resolver's AD bit (RFC 4033-4035, RFC 5155, RFC 6840): the chain
/// of trust is built from the root key (trust anchors KSK-2017 and KSK-2024) down through DS and DNSKEY; signatures
/// with RSA/SHA-256 (8), ECDSA P-256/SHA-256 (13) and P-384/SHA-384 (14) are checked with System.Security.Cryptography;
/// absence is proven with NSEC or NSEC3. Zones signed only with other algorithms are treated as insecure (RFC 4035 5.2).
/// Used for DANE (RFC 7672) when sending.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "SHA-1 is required by RFC 4034 (DS digest type 1) and RFC 5155 (NSEC3 hash); it is not used for signatures.")]
public sealed class DnssecValidator(DnsClient client, Func<DateTimeOffset>? clock = null)
{
    /// <summary>Root zone trust anchors (IANA root-anchors.xml): KSK-2017 and KSK-2024, SHA-256 DS digests.</summary>
    public static readonly IReadOnlyList<DsData> RootAnchors =
    [
        new(20326, 8, 2, Convert.FromHexString("E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D")),
        new(38696, 8, 2, Convert.FromHexString("683D2D0ACB8C9B712A1948B27F741219298D0A450D612C483AF444A4C0FB2B16")),
    ];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, Zone Zone)> _zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Anchors used instead of the root (tests with a privately signed zone).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DsData>> ExtraAnchors { get; init; } = new Dictionary<string, IReadOnlyList<DsData>>(StringComparer.OrdinalIgnoreCase);

    private sealed record Zone(string Name, DnssecStatus Status, IReadOnlyList<DnsRecord> Keys, string? Reason = null);

    public async Task<DnssecResult> ResolveAsync(string name, DnsType type, CancellationToken ct = default)
    {
        var qname = Norm(name);
        var zone = await ZoneOfAsync(qname, ct);
        if (zone.Status != DnssecStatus.Secure)
        {
            var plain = await client.QueryDnssecAsync(qname, type, ct);
            return new DnssecResult(zone.Status, plain?.RCode ?? DnsRCode.ServFail, plain?.Answers.Where(r => r.Type == type).ToList() ?? [], zone.Reason);
        }

        var msg = await client.QueryDnssecAsync(qname, type, ct);
        if (msg is null)
        {
            return new DnssecResult(DnssecStatus.Indeterminate, DnsRCode.ServFail, [], "no answer");
        }

        var records = msg.Answers.Where(r => r.Type == type).ToList();
        if (msg.RCode == DnsRCode.NoError && msg.Answers.Any(r => r.Type != DnsType.RRSIG))
        {
            // Every RRset of the answer (CNAME chain included) must verify with the keys of its own zone.
            foreach (var set in msg.Answers.Where(r => r.Type != DnsType.RRSIG).GroupBy(r => (Norm(r.Name), r.Type)))
            {
                var owner = set.Key.Item1;
                var z = NameEq(owner, qname) ? zone : await ZoneOfAsync(owner, ct);
                if (z.Status == DnssecStatus.Insecure)
                {
                    return new DnssecResult(DnssecStatus.Insecure, msg.RCode, records, "CNAME into an insecure zone");
                }

                string? why = z.Reason;
                if (z.Status != DnssecStatus.Secure || !Verify(set.ToList(), Sigs(msg.Answers, owner, set.Key.Type), z, out why))
                {
                    return new DnssecResult(DnssecStatus.Bogus, msg.RCode, records, $"{owner} {set.Key.Type}: {why}");
                }
            }

            return new DnssecResult(DnssecStatus.Secure, msg.RCode, records);
        }

        // No data: the denial must be proven by the zone (RFC 4035 5.4, RFC 5155 8).
        var proof = Denial(msg, qname, type, zone);
        return proof is null
            ? new DnssecResult(DnssecStatus.Secure, msg.RCode, [])
            : new DnssecResult(DnssecStatus.Bogus, msg.RCode, [], proof);
    }

    // ---------------- chain of trust ----------------

    /// <summary>The deepest zone containing <paramref name="name"/> and whether it is secure, walking DS records down from the root.</summary>
    private async Task<Zone> ZoneOfAsync(string name, CancellationToken ct)
    {
        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var start = labels.Length - 1;
        Zone zone;

        // A configured anchor (a privately signed zone) replaces the walk from the root above it.
        var anchored = Enumerable.Range(0, labels.Length).FirstOrDefault(i => ExtraAnchors.ContainsKey(string.Join('.', labels[i..]) + "."), -1);
        if (anchored >= 0)
        {
            var apex = string.Join('.', labels[anchored..]) + ".";
            zone = await CachedAsync(apex, () => KeysAsync(apex, ExtraAnchors[apex], ct));
            start = anchored - 1;
        }
        else
        {
            zone = await RootAsync(ct);
        }

        for (var i = start; i >= 0 && zone.Status == DnssecStatus.Secure; i--)
        {
            var child = string.Join('.', labels[i..]) + ".";
            zone = await ChildAsync(zone, child, ct);
        }

        return zone;
    }

    private async Task<Zone> RootAsync(CancellationToken ct) => await CachedAsync(".", async () =>
    {
        return await KeysAsync(".", RootAnchors, ct);
    });

    /// <summary>
    /// From a secure parent to the name below it: a signed DS makes it a secure zone; a proven absence of DS at a
    /// delegation makes it (and everything below) insecure; no delegation keeps the parent.
    /// </summary>
    private async Task<Zone> ChildAsync(Zone parent, string child, CancellationToken ct)
    {
        if (ExtraAnchors.TryGetValue(child, out var anchors))
        {
            return await CachedAsync(child, () => KeysAsync(child, anchors, ct));
        }

        if (_zones.TryGetValue(child, out var hit) && hit.Until > _clock())
        {
            return hit.Zone.Name == child || hit.Zone.Status != DnssecStatus.Secure ? hit.Zone : parent;
        }

        var msg = await client.QueryDnssecAsync(child, DnsType.DS, ct);
        if (msg is null)
        {
            return new Zone(child, DnssecStatus.Indeterminate, [], "no answer for DS " + child);
        }

        var ds = msg.Answers.Where(r => r.Type == DnsType.DS && NameEq(r.Name, child)).ToList();
        Zone result;
        if (ds.Count > 0)
        {
            if (!Verify(ds, Sigs(msg.Answers, child, DnsType.DS), parent, out var why))
            {
                result = new Zone(child, DnssecStatus.Bogus, [], "DS " + child + ": " + why);
            }
            else
            {
                result = await KeysAsync(child, ds.Select(r => (DsData)r.Data).ToList(), ct);
            }
        }
        else
        {
            // No DS: is it a delegation (insecure child) or just a name inside the parent zone?
            var (proven, delegation, optOut, reason) = NoDs(msg, child, parent);
            if (!proven)
            {
                result = new Zone(child, DnssecStatus.Bogus, [], "no proof of missing DS for " + child + ": " + reason);
            }
            else if (delegation || optOut)
            {
                result = new Zone(child, DnssecStatus.Insecure, [], "insecure delegation at " + child);
            }
            else
            {
                _zones[child] = (_clock().AddMinutes(30), parent);
                return parent;
            }
        }

        _zones[child] = (_clock().AddMinutes(result.Status == DnssecStatus.Secure ? 60 : 5), result);
        return result;
    }

    private async Task<Zone> CachedAsync(string name, Func<Task<Zone>> load)
    {
        if (_zones.TryGetValue(name, out var hit) && hit.Until > _clock())
        {
            return hit.Zone;
        }

        var z = await load();
        _zones[name] = (_clock().AddMinutes(z.Status == DnssecStatus.Secure ? 60 : 5), z);
        return z;
    }

    /// <summary>DNSKEY of a zone, authenticated by one of the DS digests; the key set must be signed by a key the DS names.</summary>
    private async Task<Zone> KeysAsync(string zone, IReadOnlyList<DsData> ds, CancellationToken ct)
    {
        var usable = ds.Where(d => SupportedAlgorithm(d.Algorithm) && d.DigestType is 1 or 2 or 4).ToList();
        if (usable.Count == 0)
        {
            return new Zone(zone, DnssecStatus.Insecure, [], "no supported algorithm in DS of " + zone); // RFC 4035 5.2
        }

        var msg = await client.QueryDnssecAsync(zone, DnsType.DNSKEY, ct);
        var keys = msg?.Answers.Where(r => r.Type == DnsType.DNSKEY && NameEq(r.Name, zone)).ToList() ?? [];
        if (keys.Count == 0)
        {
            // No root key at all: the resolver does not serve DNSSEC (or no Internet DNS) - not an attack on one name.
            return zone == "."
                ? new Zone(zone, DnssecStatus.Indeterminate, [], "the resolver returns no DNSSEC data for the root")
                : new Zone(zone, DnssecStatus.Bogus, [], "no DNSKEY for " + zone);
        }

        var entry = keys.Where(k => usable.Any(d => Matches(d, zone, k))).ToList();
        if (entry.Count == 0)
        {
            return new Zone(zone, DnssecStatus.Bogus, [], "no DNSKEY of " + zone + " matches its DS");
        }

        var self = new Zone(zone, DnssecStatus.Secure, entry);
        return Verify(keys, Sigs(msg!.Answers, zone, DnsType.DNSKEY), self, out var why)
            ? new Zone(zone, DnssecStatus.Secure, keys)
            : new Zone(zone, DnssecStatus.Bogus, [], "DNSKEY " + zone + ": " + why);
    }

    // ---------------- denial of existence ----------------

    /// <summary>Proof that <paramref name="child"/> has no DS in the parent; and whether it is a delegation (NS) or opt-out.</summary>
    private (bool Proven, bool Delegation, bool OptOut, string? Reason) NoDs(DnsMessage msg, string child, Zone parent)
    {
        var nsec = msg.Authority.Where(r => r.Type == DnsType.NSEC).ToList();
        foreach (var n in nsec.Where(n => NameEq(n.Name, child)))
        {
            if (!Verify([n], Sigs(msg.Authority, Norm(n.Name), DnsType.NSEC), parent, out var why))
            {
                return (false, false, false, why);
            }

            var d = (NsecData)n.Data;
            return d.Types.Contains(DnsType.DS) ? (false, false, false, "NSEC says DS exists") : (true, IsCut(d.Types), false, null);
        }

        foreach (var n in nsec.Where(n => Covers(Norm(n.Name), ((NsecData)n.Data).NextName, child)))
        {
            return Verify([n], Sigs(msg.Authority, Norm(n.Name), DnsType.NSEC), parent, out var why) ? (true, false, false, null) : (false, false, false, why);
        }

        var nsec3 = msg.Authority.Where(r => r.Type == DnsType.NSEC3).ToList();
        if (nsec3.Count > 0)
        {
            foreach (var n in nsec3)
            {
                if (!Verify([n], Sigs(msg.Authority, Norm(n.Name), DnsType.NSEC3), parent, out var why))
                {
                    return (false, false, false, why);
                }
            }

            var p = (Nsec3Data)nsec3[0].Data;
            var h = Nsec3Hash(child, p.Salt, p.Iterations);
            foreach (var n in nsec3)
            {
                var d = (Nsec3Data)n.Data;
                if (OwnerHash(n).SequenceEqual(h))
                {
                    return d.Types.Contains(DnsType.DS) ? (false, false, false, "NSEC3 says DS exists") : (true, IsCut(d.Types), false, null);
                }
            }

            foreach (var n in nsec3)
            {
                var d = (Nsec3Data)n.Data;
                if (HashCovers(OwnerHash(n), d.NextHashedOwner, h))
                {
                    return (true, false, d.OptOut, null); // opt-out: an unsigned delegation may hide here (RFC 5155 6)
                }
            }

            return (false, false, false, "no NSEC3 matches or covers " + child);
        }

        return (false, false, false, "no NSEC/NSEC3 in a signed zone");
    }

    /// <summary>
    /// A zone cut as seen from the parent (RFC 4035 5.2, RFC 6840 4.4): NS without SOA, and nothing the parent cannot
    /// own there. "Black lies" denials (minimal online signing: CoreDNS, Cloudflare) list NS together with A, TXT ...
    /// for an ordinary name; that is not a delegation.
    /// </summary>
    private static bool IsCut(IReadOnlySet<DnsType> types) =>
        types.Contains(DnsType.NS) && !types.Contains(DnsType.SOA) && types.All(t => t is DnsType.NS or DnsType.DS or DnsType.NSEC or DnsType.RRSIG);

    /// <summary>Null when the NODATA / NXDOMAIN answer is proven, otherwise why not.</summary>
    private string? Denial(DnsMessage msg, string qname, DnsType type, Zone zone)
    {
        var auth = msg.Authority;
        foreach (var set in auth.Where(r => r.Type is DnsType.NSEC or DnsType.NSEC3).GroupBy(r => (Norm(r.Name), r.Type)))
        {
            if (!Verify(set.ToList(), Sigs(auth, set.Key.Item1, set.Key.Type), zone, out var why))
            {
                return why;
            }
        }

        var nsec = auth.Where(r => r.Type == DnsType.NSEC).ToList();
        if (nsec.Count > 0)
        {
            if (msg.RCode == DnsRCode.NoError)
            {
                var match = nsec.FirstOrDefault(n => NameEq(n.Name, qname));
                if (match is not null)
                {
                    var t = ((NsecData)match.Data).Types;
                    return t.Contains(type) || t.Contains(DnsType.CNAME) ? "NSEC lists the type" : null;
                }

                // Wildcard NODATA (RFC 4035 3.1.3.4): accepted when an NSEC covers the name.
                return nsec.Any(n => Covers(Norm(n.Name), ((NsecData)n.Data).NextName, qname)) ? null : "no NSEC for " + qname;
            }

            return nsec.Any(n => Covers(Norm(n.Name), ((NsecData)n.Data).NextName, qname)) ? null : "no NSEC covers " + qname;
        }

        var nsec3 = auth.Where(r => r.Type == DnsType.NSEC3).ToList();
        if (nsec3.Count == 0)
        {
            return "no NSEC/NSEC3 for a denial in a signed zone";
        }

        var p = (Nsec3Data)nsec3[0].Data;
        byte[] H(string n) => Nsec3Hash(n, p.Salt, p.Iterations);
        if (msg.RCode == DnsRCode.NoError)
        {
            var match = nsec3.FirstOrDefault(n => OwnerHash(n).SequenceEqual(H(qname)));
            if (match is not null)
            {
                var t = ((Nsec3Data)match.Data).Types;
                return t.Contains(type) || t.Contains(DnsType.CNAME) ? "NSEC3 lists the type" : null;
            }

            // DS NODATA under opt-out (RFC 5155 8.6) or wildcard NODATA: closest encloser proof below.
        }

        // Closest encloser proof (RFC 5155 8.3): an ancestor matched, the next closer name covered.
        var labels = qname.TrimEnd('.').Split('.');
        for (var i = 1; i <= labels.Length; i++)
        {
            var ce = i == labels.Length ? "." : string.Join('.', labels[i..]) + ".";
            if (!nsec3.Any(n => OwnerHash(n).SequenceEqual(H(ce))))
            {
                continue;
            }

            var nextCloser = string.Join('.', labels[(i - 1)..]) + ".";
            var cover = nsec3.FirstOrDefault(n => HashCovers(OwnerHash(n), ((Nsec3Data)n.Data).NextHashedOwner, H(nextCloser)));
            if (cover is null)
            {
                return "next closer name not covered";
            }

            return null;
        }

        return "no closest encloser proof for " + qname;
    }

    // ---------------- signatures ----------------

    private static List<RrsigData> Sigs(IEnumerable<DnsRecord> section, string owner, DnsType covered) =>
        section.Where(r => r.Type == DnsType.RRSIG && NameEq(r.Name, owner)).Select(r => (RrsigData)r.Data).Where(s => s.TypeCovered == covered).ToList();

    /// <summary>True when one signature over the RRset verifies with a key of the zone and is in its validity window.</summary>
    private bool Verify(List<DnsRecord> rrset, List<RrsigData> sigs, Zone zone, out string? why)
    {
        why = sigs.Count == 0 ? "unsigned" : "no valid signature";
        var now = (uint)_clock().ToUnixTimeSeconds();
        foreach (var sig in sigs)
        {
            if (!NameEq(sig.SignerName, zone.Name))
            {
                why = "signed by " + sig.SignerName + ", expected " + zone.Name;
                continue;
            }

            if (SerialLess(now, sig.Inception) || SerialLess(sig.Expiration, now))
            {
                why = "signature outside its validity period";
                continue;
            }

            foreach (var key in zone.Keys.Where(k => k.Data is DnskeyData d && d.Algorithm == sig.Algorithm && d.IsZoneKey && KeyTag(k) == sig.KeyTag))
            {
                if (Check(rrset, sig, (DnskeyData)key.Data))
                {
                    why = null;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SerialLess(uint a, uint b) => a != b && (int)(b - a) > 0; // RFC 1982 arithmetic

    private static bool SupportedAlgorithm(byte a) => a is 8 or 13 or 14;

    /// <summary>RFC 4034 3.1.8.1: RRSIG RDATA (without the signature) followed by the RRset in canonical form and order.</summary>
    private static bool Check(List<DnsRecord> rrset, RrsigData sig, DnskeyData key)
    {
        if (!SupportedAlgorithm(sig.Algorithm))
        {
            return false;
        }

        var data = new List<byte>();
        Span<byte> head = stackalloc byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)sig.TypeCovered);
        head[2] = sig.Algorithm;
        head[3] = sig.Labels;
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], sig.OriginalTtl);
        BinaryPrimitives.WriteUInt32BigEndian(head[8..], sig.Expiration);
        BinaryPrimitives.WriteUInt32BigEndian(head[12..], sig.Inception);
        BinaryPrimitives.WriteUInt16BigEndian(head[16..], sig.KeyTag);
        data.AddRange(head.ToArray());
        data.AddRange(DnsWire.CanonicalName(sig.SignerName));
        var owner = rrset[0].Name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var ownerName = owner.Length > sig.Labels ? "*." + string.Join('.', owner[^sig.Labels..]) : string.Join('.', owner); // wildcard expansion (RFC 4035 5.3.2)
        var ownerWire = DnsWire.CanonicalName(ownerName);
        foreach (var rdata in rrset.Select(DnsWire.CanonicalRdata).Distinct(ByteComparer.Instance).OrderBy(r => r, ByteComparer.Instance))
        {
            data.AddRange(ownerWire);
            var rrHead = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(rrHead, (ushort)rrset[0].Type);
            BinaryPrimitives.WriteUInt16BigEndian(rrHead.AsSpan(2), rrset[0].Class);
            BinaryPrimitives.WriteUInt32BigEndian(rrHead.AsSpan(4), sig.OriginalTtl);
            BinaryPrimitives.WriteUInt16BigEndian(rrHead.AsSpan(8), (ushort)rdata.Length);
            data.AddRange(rrHead);
            data.AddRange(rdata);
        }

        var signed = data.ToArray();
        try
        {
            switch (sig.Algorithm)
            {
                case 8:
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportParameters(RsaKey(key.PublicKey));
                        return rsa.VerifyData(signed, sig.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }

                case 13 or 14:
                    {
                        var size = sig.Algorithm == 13 ? 32 : 48;
                        if (key.PublicKey.Length != 2 * size)
                        {
                            return false;
                        }

                        using var ecdsa = ECDsa.Create(new ECParameters
                        {
                            Curve = sig.Algorithm == 13 ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384,
                            Q = new ECPoint { X = key.PublicKey[..size], Y = key.PublicKey[size..] },
                        });
                        return ecdsa.VerifyData(signed, sig.Signature, sig.Algorithm == 13 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA384,
                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    }

                default:
                    return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>RSA public key in DNSKEY format (RFC 3110 2).</summary>
    private static RSAParameters RsaKey(byte[] k)
    {
        int expLen, off;
        if (k[0] == 0)
        {
            expLen = (k[1] << 8) | k[2];
            off = 3;
        }
        else
        {
            expLen = k[0];
            off = 1;
        }

        return new RSAParameters { Exponent = k[off..(off + expLen)], Modulus = k[(off + expLen)..] };
    }

    /// <summary>Key tag (RFC 4034 appendix B).</summary>
    public static ushort KeyTag(DnsRecord dnskey)
    {
        var rd = dnskey.RawRdata;
        uint ac = 0;
        for (var i = 0; i < rd.Length; i++)
        {
            ac += (i & 1) == 1 ? rd[i] : (uint)rd[i] << 8;
        }

        ac += (ac >> 16) & 0xFFFF;
        return (ushort)(ac & 0xFFFF);
    }

    /// <summary>Whether a DNSKEY is the one a DS points to (RFC 4034 5.1.4: digest over owner name and DNSKEY RDATA).</summary>
    private static bool Matches(DsData ds, string zone, DnsRecord key)
    {
        if (key.Data is not DnskeyData k || k.Algorithm != ds.Algorithm || KeyTag(key) != ds.KeyTag)
        {
            return false;
        }

        byte[] input = [.. DnsWire.CanonicalName(zone), .. key.RawRdata];
        var digest = ds.DigestType switch
        {
            1 => SHA1.HashData(input),
            2 => SHA256.HashData(input),
            4 => SHA384.HashData(input),
            _ => [],
        };
        return CryptographicOperations.FixedTimeEquals(digest, ds.Digest);
    }

    // ---------------- names and NSEC3 hashes ----------------

    public static string Norm(string name) => name.Trim().TrimEnd('.').ToLowerInvariant() + ".";

    private static bool NameEq(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical DNS name order (RFC 4034 6.1): labels compared from the right, lower-case bytes.</summary>
    public static int CanonicalCompare(string a, string b)
    {
        var la = a.TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        var lb = b.TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = la.Length - 1, j = lb.Length - 1; i >= 0 || j >= 0; i--, j--)
        {
            if (i < 0)
            {
                return -1;
            }

            if (j < 0)
            {
                return 1;
            }

            var c = ByteComparer.Instance.Compare(Encoding.ASCII.GetBytes(la[i]), Encoding.ASCII.GetBytes(lb[j]));
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>Whether NSEC (owner → next) covers <paramref name="name"/>; the last NSEC wraps to the zone apex.</summary>
    private static bool Covers(string owner, string next, string name)
    {
        var a = CanonicalCompare(owner, name);
        var b = CanonicalCompare(name, next);
        return CanonicalCompare(owner, next) < 0 ? a < 0 && b < 0 : a < 0 || b < 0;
    }

    /// <summary>NSEC3 hash (RFC 5155 5): iterated SHA-1 of the canonical name with the salt.</summary>
    public static byte[] Nsec3Hash(string name, byte[] salt, ushort iterations)
    {
        var x = SHA1.HashData([.. DnsWire.CanonicalName(name), .. salt]);
        for (var i = 0; i < iterations; i++)
        {
            x = SHA1.HashData([.. x, .. salt]);
        }

        return x;
    }

    private static byte[] OwnerHash(DnsRecord nsec3) => Base32Hex(nsec3.Name.Split('.')[0]);

    private static bool HashCovers(byte[] owner, byte[] next, byte[] h)
    {
        var a = ByteComparer.Instance.Compare(owner, h);
        var b = ByteComparer.Instance.Compare(h, next);
        return ByteComparer.Instance.Compare(owner, next) < 0 ? a < 0 && b < 0 : a < 0 || b < 0;
    }

    /// <summary>Base32 with the extended hex alphabet (RFC 4648 7), as used in NSEC3 owner names.</summary>
    public static byte[] Base32Hex(string text)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuv";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in text.ToLowerInvariant())
        {
            var v = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return [];
            }

            value = (value << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((value >> bits) & 0xFF));
            }
        }

        return [.. output];
    }

    private sealed class ByteComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var h = new HashCode();
            h.AddBytes(obj);
            return h.ToHashCode();
        }
    }
}

/// <summary>Parses configured trust anchors: "zone keytag algorithm digesttype hexdigest" entries separated by ';'.</summary>
public static class DnssecAnchors
{
    public static IReadOnlyDictionary<string, IReadOnlyList<DsData>> Parse(string? text)
    {
        var result = new Dictionary<string, IReadOnlyList<DsData>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5)
            {
                throw new FormatException("DNSSEC anchor needs 'zone keytag algorithm digesttype hexdigest': " + entry);
            }

            var zone = DnssecValidator.Norm(f[0]);
            var ds = new DsData(ushort.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), byte.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                byte.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(f[4]));
            result[zone] = result.TryGetValue(zone, out var list) ? [.. list, ds] : [ds];
        }

        return result;
    }
}
