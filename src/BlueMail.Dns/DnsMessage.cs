using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace BlueMail.Dns;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "RFC record type mnemonics.")]
public enum DnsType : ushort
{
    A = 1, NS = 2, CNAME = 5, SOA = 6, PTR = 12, MX = 15, TXT = 16, AAAA = 28, SRV = 33, OPT = 41,
    DS = 43, RRSIG = 46, NSEC = 47, DNSKEY = 48, NSEC3 = 50, TLSA = 52, CAA = 257, ANY = 255,
}

public enum DnsRCode : byte { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }

/// <summary>A resource record. <see cref="Data"/> is the parsed RDATA (see the record types below).</summary>
public sealed record DnsRecord(string Name, DnsType Type, ushort Class, uint Ttl, object Data)
{
    /// <summary>RDATA as received (names inside may be compressed: use <see cref="DnsWire.CanonicalRdata"/> for DNSSEC).</summary>
    public byte[] RawRdata { get; init; } = [];

    public override string ToString() => $"{Name} {Ttl} {Type} {Data}";
}

public sealed record MxData(ushort Preference, string Exchange)
{
    public override string ToString() => $"{Preference} {Exchange}";
}

public sealed record SoaData(string MName, string RName, uint Serial, uint Refresh, uint Retry, uint Expire, uint Minimum);

public sealed record SrvData(ushort Priority, ushort Weight, ushort Port, string Target);

public sealed record TlsaData(byte Usage, byte Selector, byte MatchingType, byte[] Certificate);

public sealed record CaaData(byte Flags, string Tag, string Value);

/// <summary>TXT RDATA: the character-strings as received, and joined (RFC 7208 3.3 for SPF, RFC 6376 for DKIM).</summary>
public sealed record TxtData(IReadOnlyList<byte[]> Strings)
{
    public string Text => Encoding.UTF8.GetString(Strings.SelectMany(s => s).ToArray());

    public override string ToString() => Text;
}

public sealed record UnknownData(byte[] Raw);

/// <summary>RRSIG (RFC 4034 3.1).</summary>
public sealed record RrsigData(DnsType TypeCovered, byte Algorithm, byte Labels, uint OriginalTtl, uint Expiration, uint Inception, ushort KeyTag,
    string SignerName, byte[] Signature);

/// <summary>DNSKEY (RFC 4034 2.1).</summary>
public sealed record DnskeyData(ushort Flags, byte Protocol, byte Algorithm, byte[] PublicKey)
{
    public bool IsZoneKey => (Flags & 0x0100) != 0;
    public bool IsSecureEntryPoint => (Flags & 0x0001) != 0;
}

/// <summary>DS (RFC 4034 5.1).</summary>
public sealed record DsData(ushort KeyTag, byte Algorithm, byte DigestType, byte[] Digest);

/// <summary>NSEC (RFC 4034 4.1).</summary>
public sealed record NsecData(string NextName, IReadOnlySet<DnsType> Types);

/// <summary>NSEC3 (RFC 5155 3.1).</summary>
public sealed record Nsec3Data(byte HashAlgorithm, byte Flags, ushort Iterations, byte[] Salt, byte[] NextHashedOwner, IReadOnlySet<DnsType> Types)
{
    public bool OptOut => (Flags & 0x01) != 0;
}

/// <summary>A parsed DNS message (RFC 1035 4.1).</summary>
public sealed class DnsMessage
{
    public ushort Id { get; init; }
    public bool IsResponse { get; init; }
    public bool Truncated { get; init; }
    public bool RecursionAvailable { get; init; }
    public bool AuthenticData { get; init; }
    public DnsRCode RCode { get; init; }
    public string QuestionName { get; init; } = "";
    public DnsType QuestionType { get; init; }
    public IReadOnlyList<DnsRecord> Answers { get; init; } = [];
    public IReadOnlyList<DnsRecord> Authority { get; init; } = [];
    public IReadOnlyList<DnsRecord> Additional { get; init; } = [];
}

public sealed class DnsFormatException(string message) : Exception(message);

/// <summary>
/// Wire format encoder/decoder. Decoding defends against hostile packets (PROMPT principle 7): every read
/// is bounds-checked, compression pointers may only point backwards and at most 64 jumps are followed per
/// name, names are limited to 255 octets and labels to 63.
/// </summary>
public static class DnsWire
{
    private const int MaxPointerJumps = 64;

    public static byte[] EncodeQuery(ushort id, string name, DnsType type, ushort udpPayload, bool dnssecOk, bool checkingDisabled = false)
    {
        var buf = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01; // RD
        header[3] = checkingDisabled ? (byte)0x10 : (byte)0x00; // CD: we validate DNSSEC ourselves
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], 1); // ARCOUNT (OPT)
        buf.AddRange(header.ToArray());
        WriteName(buf, name);
        buf.Add((byte)((ushort)type >> 8));
        buf.Add((byte)(ushort)type);
        buf.Add(0);
        buf.Add(1); // IN

        // EDNS0 OPT pseudo-record (RFC 6891 6.1.2)
        buf.Add(0); // root name
        buf.Add(0);
        buf.Add((byte)DnsType.OPT);
        buf.Add((byte)(udpPayload >> 8));
        buf.Add((byte)udpPayload);
        buf.Add(0); // extended RCODE
        buf.Add(0); // version
        buf.Add((byte)(dnssecOk ? 0x80 : 0)); // DO bit
        buf.Add(0);
        buf.Add(0);
        buf.Add(0); // RDLEN
        return [.. buf];
    }

    private static void WriteName(List<byte> buf, string name)
    {
        var n = name.TrimEnd('.');
        if (n.Length > 0)
        {
            foreach (var label in n.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                {
                    throw new ArgumentException($"Invalid DNS label in '{name}'.", nameof(name));
                }

                buf.Add((byte)bytes.Length);
                buf.AddRange(bytes);
            }
        }

        buf.Add(0);
    }

    public static DnsMessage Decode(ReadOnlySpan<byte> m)
    {
        if (m.Length < 12)
        {
            throw new DnsFormatException("Message shorter than header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(m);
        var flags1 = m[2];
        var flags2 = m[3];
        var qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        var ns = BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
        var ar = BinaryPrimitives.ReadUInt16BigEndian(m[10..]);
        if (qd > 1)
        {
            throw new DnsFormatException("More than one question.");
        }

        var pos = 12;
        string qname = "";
        DnsType qtype = 0;
        if (qd == 1)
        {
            qname = ReadName(m, ref pos);
            Need(m, pos, 4);
            qtype = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            pos += 4;
        }

        var answers = ReadRecords(m, ref pos, an);
        var authority = ReadRecords(m, ref pos, ns);
        var additional = ReadRecords(m, ref pos, ar);
        var extRcode = 0;
        foreach (var opt in additional.Where(r => r.Type == DnsType.OPT))
        {
            extRcode = (int)(opt.Ttl >> 24) << 4;
        }

        return new DnsMessage
        {
            Id = id,
            IsResponse = (flags1 & 0x80) != 0,
            Truncated = (flags1 & 0x02) != 0,
            RecursionAvailable = (flags2 & 0x80) != 0,
            AuthenticData = (flags2 & 0x20) != 0,
            RCode = (DnsRCode)((flags2 & 0x0F) | extRcode),
            QuestionName = qname,
            QuestionType = qtype,
            Answers = answers,
            Authority = authority,
            Additional = additional,
        };
    }

    private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> m, ref int pos, int count)
    {
        var list = new List<DnsRecord>(Math.Min(count, 64));
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(m, ref pos);
            Need(m, pos, 10);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(m[(pos + 4)..]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            pos += 10;
            Need(m, pos, rdlen);
            var rdStart = pos;
            var data = ParseRdata(m, type, rdStart, rdlen);
            pos = rdStart + rdlen;
            list.Add(new DnsRecord(name, type, cls, ttl, data) { RawRdata = m.Slice(rdStart, rdlen).ToArray() });
        }

        return list;
    }

    private static object ParseRdata(ReadOnlySpan<byte> m, DnsType type, int start, int len)
    {
        var rd = m.Slice(start, len);
        var p = start;
        switch (type)
        {
            case DnsType.A when len == 4:
                return new IPAddress(rd);
            case DnsType.AAAA when len == 16:
                return new IPAddress(rd);
            case DnsType.CNAME or DnsType.NS or DnsType.PTR:
                return ReadName(m, ref p, start + len);
            case DnsType.MX when len >= 3:
                p += 2;
                return new MxData(BinaryPrimitives.ReadUInt16BigEndian(rd), ReadName(m, ref p, start + len));
            case DnsType.TXT:
                var strings = new List<byte[]>();
                var i = 0;
                while (i < len)
                {
                    var l = rd[i];
                    if (i + 1 + l > len)
                    {
                        throw new DnsFormatException("TXT string overruns RDATA.");
                    }

                    strings.Add(rd.Slice(i + 1, l).ToArray());
                    i += 1 + l;
                }

                return new TxtData(strings);
            case DnsType.SOA:
                var mname = ReadName(m, ref p, start + len);
                var rname = ReadName(m, ref p, start + len);
                if (p + 20 > start + len)
                {
                    throw new DnsFormatException("SOA RDATA too short.");
                }

                return new SoaData(mname, rname, U32(m, p), U32(m, p + 4), U32(m, p + 8), U32(m, p + 12), U32(m, p + 16));
            case DnsType.SRV when len >= 7:
                p += 6;
                return new SrvData(BinaryPrimitives.ReadUInt16BigEndian(rd), BinaryPrimitives.ReadUInt16BigEndian(rd[2..]),
                    BinaryPrimitives.ReadUInt16BigEndian(rd[4..]), ReadName(m, ref p, start + len));
            case DnsType.TLSA when len >= 3:
                return new TlsaData(rd[0], rd[1], rd[2], rd[3..].ToArray());
            case DnsType.RRSIG when len >= 19:
                {
                    p = start + 18;
                    var signer = ReadName(m, ref p, start + len);
                    if (p > start + len)
                    {
                        throw new DnsFormatException("RRSIG signer runs past its RDATA"); // BUG-067
                    }

                    return new RrsigData((DnsType)BinaryPrimitives.ReadUInt16BigEndian(rd), rd[2], rd[3], BinaryPrimitives.ReadUInt32BigEndian(rd[4..]),
                        BinaryPrimitives.ReadUInt32BigEndian(rd[8..]), BinaryPrimitives.ReadUInt32BigEndian(rd[12..]), BinaryPrimitives.ReadUInt16BigEndian(rd[16..]),
                        signer, m[p..(start + len)].ToArray());
                }

            case DnsType.DNSKEY when len >= 4:
                return new DnskeyData(BinaryPrimitives.ReadUInt16BigEndian(rd), rd[2], rd[3], rd[4..].ToArray());
            case DnsType.DS when len >= 4:
                return new DsData(BinaryPrimitives.ReadUInt16BigEndian(rd), rd[2], rd[3], rd[4..].ToArray());
            case DnsType.NSEC:
                {
                    var next = ReadName(m, ref p, start + len);
                    if (p > start + len)
                    {
                        throw new DnsFormatException("NSEC next name runs past its RDATA");
                    }

                    return new NsecData(next, TypeBitmap(m[p..(start + len)]));
                }

            case DnsType.NSEC3 when len >= 5:
                {
                    var saltLen = rd[4];
                    if (5 + saltLen >= len)
                    {
                        throw new DnsFormatException("NSEC3 salt runs past its RDATA"); // BUG-066: bounds are the RDATA's, not the packet's
                    }

                    var hashLen = rd[5 + saltLen];
                    var bitmapStart = 6 + saltLen + hashLen;
                    if (bitmapStart > len)
                    {
                        throw new DnsFormatException("NSEC3 too short");
                    }

                    return new Nsec3Data(rd[0], rd[1], BinaryPrimitives.ReadUInt16BigEndian(rd[2..]), rd.Slice(5, saltLen).ToArray(),
                        rd.Slice(6 + saltLen, hashLen).ToArray(), TypeBitmap(rd[bitmapStart..]));
                }
            case DnsType.CAA when len >= 2 && 2 + rd[1] <= len:
                return new CaaData(rd[0], Encoding.ASCII.GetString(rd.Slice(2, rd[1])), Encoding.UTF8.GetString(rd[(2 + rd[1])..]));
            default:
                return new UnknownData(rd.ToArray());
        }
    }

    private static uint U32(ReadOnlySpan<byte> m, int p) => BinaryPrimitives.ReadUInt32BigEndian(m[p..]);

    /// <summary>Type bit maps of NSEC/NSEC3 (RFC 4034 4.1.2).</summary>
    internal static HashSet<DnsType> TypeBitmap(ReadOnlySpan<byte> b)
    {
        var set = new HashSet<DnsType>();
        var i = 0;
        while (i + 2 <= b.Length)
        {
            int window = b[i], len = b[i + 1];
            if (len is 0 or > 32 || i + 2 + len > b.Length)
            {
                throw new DnsFormatException("Bad type bitmap");
            }

            for (var j = 0; j < len; j++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((b[i + 2 + j] & (0x80 >> bit)) != 0)
                    {
                        set.Add((DnsType)(window * 256 + j * 8 + bit));
                    }
                }
            }

            i += 2 + len;
        }

        return set;
    }

    /// <summary>A name in canonical wire form (RFC 4034 6.2): uncompressed, lower-case labels.</summary>
    public static byte[] CanonicalName(string name)
    {
        var buf = new List<byte>();
        WriteName(buf, name.ToLowerInvariant());
        return [.. buf];
    }

    /// <summary>
    /// Canonical RDATA (RFC 4034 6.2 with RFC 6840 5.1): names in the record types that embed them are written
    /// uncompressed and lower-case; other RDATA is used as received.
    /// </summary>
    public static byte[] CanonicalRdata(DnsRecord r)
    {
        var buf = new List<byte>();
        switch (r.Data)
        {
            case string name when r.Type is DnsType.NS or DnsType.CNAME or DnsType.PTR:
                WriteName(buf, name.ToLowerInvariant());
                return [.. buf];
            case MxData mx:
                buf.Add((byte)(mx.Preference >> 8));
                buf.Add((byte)mx.Preference);
                WriteName(buf, mx.Exchange.ToLowerInvariant());
                return [.. buf];
            case SoaData soa:
                WriteName(buf, soa.MName.ToLowerInvariant());
                WriteName(buf, soa.RName.ToLowerInvariant());
                foreach (var v in new[] { soa.Serial, soa.Refresh, soa.Retry, soa.Expire, soa.Minimum })
                {
                    buf.Add((byte)(v >> 24));
                    buf.Add((byte)(v >> 16));
                    buf.Add((byte)(v >> 8));
                    buf.Add((byte)v);
                }

                return [.. buf];
            case SrvData srv:
                foreach (var v in new[] { srv.Priority, srv.Weight, srv.Port })
                {
                    buf.Add((byte)(v >> 8));
                    buf.Add((byte)v);
                }

                WriteName(buf, srv.Target.ToLowerInvariant());
                return [.. buf];
            default:
                return r.RawRdata;
        }
    }

    private static string ReadName(ReadOnlySpan<byte> m, ref int pos) => ReadName(m, ref pos, m.Length);

    /// <summary>Reads a possibly compressed name; <paramref name="pos"/> moves past it in the original location.</summary>
    private static string ReadName(ReadOnlySpan<byte> m, ref int pos, int limit)
    {
        var sb = new StringBuilder();
        var p = pos;
        var jumped = false;
        var jumps = 0;
        var total = 0;
        while (true)
        {
            if (p >= limit && !jumped)
            {
                throw new DnsFormatException("Name runs past its bounds.");
            }

            Need(m, p, 1);
            var len = m[p];
            if (len == 0)
            {
                p++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                Need(m, p, 2);
                var target = ((len & 0x3F) << 8) | m[p + 1];
                if (target >= p || ++jumps > MaxPointerJumps)
                {
                    throw new DnsFormatException("Invalid compression pointer.");
                }

                if (!jumped)
                {
                    pos = p + 2;
                }

                jumped = true;
                p = target;
                continue;
            }

            if ((len & 0xC0) != 0)
            {
                throw new DnsFormatException("Unsupported label type.");
            }

            Need(m, p + 1, len);
            total += len + 1;
            if (total > 255)
            {
                throw new DnsFormatException("Name longer than 255 octets.");
            }

            var label = m.Slice(p + 1, len);
            foreach (var b in label)
            {
                // Keep names printable and unambiguous (RFC 4343 escaping for '.' and non-printables).
                if (b == '.' || b == '\\' || b <= 32 || b >= 127)
                {
                    sb.Append('\\').Append(((int)b).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append((char)b);
                }
            }

            sb.Append('.');
            p += len + 1;
        }

        if (!jumped)
        {
            pos = p;
        }

        return sb.Length == 0 ? "." : sb.ToString();
    }

    private static void Need(ReadOnlySpan<byte> m, int pos, int count)
    {
        if (pos < 0 || count < 0 || pos + count > m.Length)
        {
            throw new DnsFormatException("Truncated message.");
        }
    }
}
