using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace BlueMail.Dns;

public enum DnsStatus
{
    /// <summary>Records of the requested type exist (possibly reached through CNAMEs).</summary>
    Ok,

    /// <summary>The name exists but has no record of the type (NODATA; RFC 2308 2.2).</summary>
    NoData,

    /// <summary>The name does not exist (RCODE 3; RFC 2308 2.1).</summary>
    NxDomain,

    /// <summary>The server failed or refused, the answer was malformed, or no server answered: a temporary error.</summary>
    TempFail,
}

public sealed record DnsResult(DnsStatus Status, IReadOnlyList<DnsRecord> Records, string CanonicalName, bool FromCache)
{
    public IEnumerable<T> Data<T>() => Records.Select(r => r.Data).OfType<T>();
}

public sealed record DnsClientOptions
{
    /// <summary>Recursive resolvers to ask, in order.</summary>
    public required IReadOnlyList<IPEndPoint> Servers { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
    public int Attempts { get; init; } = 2;

    /// <summary>EDNS0 UDP payload size advertised (RFC 6891; 1232 avoids IP fragmentation).</summary>
    public ushort UdpPayloadSize { get; init; } = 1232;

    public TimeSpan MinTtl { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxTtl { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Upper bound for caching NXDOMAIN/NODATA (RFC 2308 5 suggests 1-3 hours).</summary>
    public TimeSpan MaxNegativeTtl { get; init; } = TimeSpan.FromHours(1);

    public int MaxCacheEntries { get; init; } = 100_000;
    public int MaxCnameChain { get; init; } = 8;
}

/// <summary>
/// Stub resolver client (PROMPT 2.6.2): asks configured recursive resolvers over UDP with EDNS0, retries over
/// TCP when the answer is truncated (RFC 7766), uses a random query id and an OS-chosen random source port per
/// query and accepts an answer only if id, question and server address all match. Caches positive and
/// negative answers by TTL within a floor/ceiling, with a bounded size. Thread-safe.
/// </summary>
public sealed class DnsClient(DnsClientOptions options)
{
    private readonly ConcurrentDictionary<(string, DnsType), CacheEntry> _cache = new();

    private sealed record CacheEntry(DnsResult Result, DateTimeOffset Expires, long Order);

    private long _order;

    public DnsClientOptions Options => options;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public async Task<DnsResult> ResolveAsync(string name, DnsType type, CancellationToken ct = default)
    {
        var qname = Normalize(name);
        var chain = new List<DnsRecord>();
        var current = qname;
        for (var hop = 0; hop <= options.MaxCnameChain; hop++)
        {
            var r = await QueryCachedAsync(current, type, ct);
            if (r.Status != DnsStatus.Ok)
            {
                return r with { Records = chain.Concat(r.Records).ToList(), CanonicalName = current };
            }

            var direct = r.Records.Where(x => x.Type == type && NameEq(x.Name, current)).ToList();
            if (direct.Count > 0 || type == DnsType.CNAME)
            {
                return new DnsResult(DnsStatus.Ok, type == DnsType.CNAME ? r.Records : direct, current, r.FromCache);
            }

            // Follow the CNAME chain inside the answer first, then ask again for the final target.
            var target = current;
            var moved = true;
            while (moved)
            {
                moved = false;
                var cname = r.Records.FirstOrDefault(x => x.Type == DnsType.CNAME && NameEq(x.Name, target));
                if (cname is not null)
                {
                    chain.Add(cname);
                    target = (string)cname.Data;
                    moved = true;
                    if (chain.Count > options.MaxCnameChain)
                    {
                        return new DnsResult(DnsStatus.TempFail, chain, target, false);
                    }
                }
            }

            var final = r.Records.Where(x => x.Type == type && NameEq(x.Name, target)).ToList();
            if (final.Count > 0)
            {
                return new DnsResult(DnsStatus.Ok, final, target, r.FromCache);
            }

            if (NameEq(target, current))
            {
                return new DnsResult(DnsStatus.NoData, [], current, r.FromCache);
            }

            current = target;
        }

        return new DnsResult(DnsStatus.TempFail, chain, current, false);
    }

    private async Task<DnsResult> QueryCachedAsync(string name, DnsType type, CancellationToken ct)
    {
        var key = (name, type);
        var now = Clock();
        if (_cache.TryGetValue(key, out var e) && e.Expires > now)
        {
            return e.Result with { FromCache = true };
        }

        var result = await QueryAsync(name, type, ct);
        if (result.Status != DnsStatus.TempFail)
        {
            var ttl = result.Status == DnsStatus.Ok
                ? TimeSpan.FromSeconds(result.Records.Count == 0 ? 0 : result.Records.Min(r => r.Ttl))
                : NegativeTtl(result);
            ttl = TimeSpan.FromTicks(Math.Clamp(ttl.Ticks, options.MinTtl.Ticks,
                (result.Status == DnsStatus.Ok ? options.MaxTtl : options.MaxNegativeTtl).Ticks));
            if (_cache.Count >= options.MaxCacheEntries)
            {
                Evict(now);
            }

            _cache[key] = new CacheEntry(result with { Records = result.Records.Where(r => r.Type != DnsType.SOA || result.Status == DnsStatus.Ok).ToList() },
                now + ttl, Interlocked.Increment(ref _order));
        }

        return result;
    }

    /// <summary>RFC 2308 5: negative TTL = min(SOA TTL, SOA MINIMUM) from the authority section.</summary>
    private static TimeSpan NegativeTtl(DnsResult r)
    {
        var soa = r.Records.FirstOrDefault(x => x.Type == DnsType.SOA);
        return soa is null ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(soa.Ttl, ((SoaData)soa.Data).Minimum));
    }

    private void Evict(DateTimeOffset now)
    {
        foreach (var (k, v) in _cache)
        {
            if (v.Expires <= now)
            {
                _cache.TryRemove(k, out _);
            }
        }

        if (_cache.Count >= options.MaxCacheEntries)
        {
            foreach (var k in _cache.OrderBy(kv => kv.Value.Order).Take(Math.Max(1, options.MaxCacheEntries / 10)).Select(kv => kv.Key).ToList())
            {
                _cache.TryRemove(k, out _);
            }
        }
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>One query to the servers in turn; no cache, no CNAME chasing.</summary>
    public async Task<DnsResult> QueryAsync(string name, DnsType type, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            foreach (var server in options.Servers)
            {
                try
                {
                    var msg = await ExchangeAsync(server, name, type, ct);
                    if (msg is null)
                    {
                        continue;
                    }

                    switch (msg.RCode)
                    {
                        case DnsRCode.NxDomain:
                            return new DnsResult(DnsStatus.NxDomain, msg.Authority.Where(r => r.Type == DnsType.SOA).ToList(), name, false);
                        case DnsRCode.NoError:
                            if (msg.Answers.Count == 0)
                            {
                                return new DnsResult(DnsStatus.NoData, msg.Authority.Where(r => r.Type == DnsType.SOA).ToList(), name, false);
                            }

                            return new DnsResult(DnsStatus.Ok, msg.Answers, name, false);
                        default:
                            continue; // SERVFAIL / REFUSED: try the next server
                    }
                }
                catch (Exception ex) when (ex is SocketException or DnsFormatException or TimeoutException or IOException)
                {
                    // next server / attempt
                }
            }
        }

        return new DnsResult(DnsStatus.TempFail, [], name, false);
    }

    private async Task<DnsMessage?> ExchangeAsync(IPEndPoint server, string name, DnsType type, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        var query = DnsWire.EncodeQuery(id, name, type, options.UdpPayloadSize, dnssecOk: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var udp = new UdpClient(server.AddressFamily); // ephemeral source port chosen by the OS per query
            await udp.SendAsync(query, server, timeout.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (!r.RemoteEndPoint.Equals(server))
                {
                    continue; // not from the server we asked: ignore (spoofing defence)
                }

                DnsMessage msg;
                try
                {
                    msg = DnsWire.Decode(r.Buffer);
                }
                catch (DnsFormatException)
                {
                    continue;
                }

                if (!Matches(msg, id, name, type))
                {
                    continue;
                }

                return msg.Truncated ? await TcpExchangeAsync(server, name, type, timeout.Token) : msg;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"DNS query {name} {type} to {server} timed out.");
        }
    }

    private static async Task<DnsMessage?> TcpExchangeAsync(IPEndPoint server, string name, DnsType type, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        var query = DnsWire.EncodeQuery(id, name, type, 65535, dnssecOk: false);
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, ct);
        await using var stream = tcp.GetStream();
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct);
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, ct);
        var len = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
        var body = new byte[len];
        await stream.ReadExactlyAsync(body, ct);
        var msg = DnsWire.Decode(body);
        return Matches(msg, id, name, type) ? msg : null;
    }

    private static bool Matches(DnsMessage msg, ushort id, string name, DnsType type) =>
        msg.IsResponse && msg.Id == id && msg.QuestionType == type && NameEq(msg.QuestionName, name);

    internal static bool NameEq(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant() + ".";

    // ---------------- typed helpers ----------------

    public async Task<(DnsStatus Status, IReadOnlyList<IPAddress> Addresses)> LookupAddressesAsync(string host, bool ipv6, CancellationToken ct = default)
    {
        var a = await ResolveAsync(host, DnsType.A, ct);
        var list = a.Data<IPAddress>().ToList();
        var status = a.Status;
        if (ipv6)
        {
            var aaaa = await ResolveAsync(host, DnsType.AAAA, ct);
            list.AddRange(aaaa.Data<IPAddress>());
            status = list.Count > 0 ? DnsStatus.Ok : a.Status == DnsStatus.TempFail || aaaa.Status == DnsStatus.TempFail ? DnsStatus.TempFail : a.Status;
        }

        return (status, list);
    }

    /// <summary>MX records sorted by preference (RFC 5321 5.1); a single "0 ." means null MX (RFC 7505).</summary>
    public async Task<(DnsStatus Status, IReadOnlyList<MxData> Mx)> LookupMxAsync(string domain, CancellationToken ct = default)
    {
        var r = await ResolveAsync(domain, DnsType.MX, ct);
        var mx = r.Data<MxData>().OrderBy(m => m.Preference).ThenBy(m => m.Exchange, StringComparer.OrdinalIgnoreCase).ToList();
        return (r.Status, mx);
    }

    /// <summary>TXT records, each with its character-strings joined.</summary>
    public async Task<(DnsStatus Status, IReadOnlyList<string> Texts)> LookupTxtAsync(string name, CancellationToken ct = default)
    {
        var r = await ResolveAsync(name, DnsType.TXT, ct);
        return (r.Status, r.Data<TxtData>().Select(t => t.Text).ToList());
    }

    public async Task<(DnsStatus Status, IReadOnlyList<string> Names)> LookupPtrAsync(IPAddress address, CancellationToken ct = default)
    {
        var r = await ResolveAsync(ReverseName(address), DnsType.PTR, ct);
        return (r.Status, r.Data<string>().ToList());
    }

    /// <summary>in-addr.arpa / ip6.arpa name of an address (RFC 1035 3.5, RFC 3596 2.5).</summary>
    public static string ReverseName(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa.";
        }

        var nibbles = new List<string>(64);
        for (var i = b.Length - 1; i >= 0; i--)
        {
            nibbles.Add((b[i] & 0xF).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
            nibbles.Add((b[i] >> 4).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join('.', nibbles) + ".ip6.arpa.";
    }
}
