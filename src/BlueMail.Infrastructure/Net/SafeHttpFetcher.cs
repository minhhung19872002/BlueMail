using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;

namespace BlueMail.Infrastructure.Net;

public sealed record FetchedImage(byte[] Content, string ContentType);

public enum FetchFailure { BadUrl, Forbidden, NotFound, NotAnImage, TooLarge, Upstream }

public sealed class FetchException(FetchFailure reason) : Exception("Image fetch failed: " + reason)
{
    public FetchFailure Reason { get; } = reason;
}

/// <summary>
/// Fetches remote images for the image proxy (PROMPT 6.3) without SSRF: only http/https on ports 80/443, the
/// address is resolved and checked on every connection (so DNS rebinding and redirects to internal hosts fail),
/// redirects are followed by hand with the same checks, size and type are limited, results are cached.
/// No cookies, no referrer, a generic user agent: the sender learns neither the reader's IP nor when they read.
/// </summary>
public sealed class SafeHttpFetcher : IDisposable
{
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // No SVG: it is a document format that can carry script if opened directly.
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/x-icon", "image/vnd.microsoft.icon",
    };

    private readonly HttpClient _http;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 200L * 1024 * 1024 });
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public SafeHttpFetcher(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueMail-ImageProxy/1.0");
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new FetchException(FetchFailure.Forbidden);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsAllowedUrl(Uri u) =>
        u.IsAbsoluteUri && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Port is 80 or 443
        && string.IsNullOrEmpty(u.UserInfo) && u.Host.Length > 0;

    /// <summary>True for globally routable unicast addresses only.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224        // this-network, private, loopback, multicast/reserved/broadcast
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)               // carrier-grade NAT
                     || (b[0] == 169 && b[1] == 254)                             // link-local, cloud metadata
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))   // IETF protocol assignments, TEST-NET-1
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))              // benchmarking
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
        {
            return false;
        }

        var v6 = ip.GetAddressBytes();
        // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) embed IPv4 addresses: judge the embedded one.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b)
        {
            return IsPublic(new IPAddress(v6.AsSpan(12, 4)));
        }

        if (v6[0] == 0x20 && v6[1] == 0x02)
        {
            return IsPublic(new IPAddress(v6.AsSpan(2, 4)));
        }

        return (v6[0] & 0xE0) == 0x20 && !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8); // global unicast 2000::/3, not documentation
    }

    /// <summary>
    /// RFC 8058 one-click unsubscribe: POST "List-Unsubscribe=One-Click" to an https URL, through the same public-address
    /// checks as images (no redirects followed: RFC 8058 section 3.2). True when the sender answered 2xx.
    /// </summary>
    public async Task<bool> PostOneClickUnsubscribeAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsAllowedUrl(uri))
        {
            throw new FetchException(FetchFailure.BadUrl);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]),
        };
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex) when (ex.InnerException is FetchException fe)
        {
            throw fe;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FetchException(FetchFailure.Upstream);
        }
    }

    public async Task<FetchedImage> FetchAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out FetchedImage? hit) && hit is not null)
        {
            return hit;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedUrl(uri))
        {
            throw new FetchException(FetchFailure.BadUrl);
        }

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("image/*");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is FetchException fe)
            {
                throw fe;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new FetchException(FetchFailure.Upstream);
            }

            using (resp)
            {
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (!IsAllowedUrl(next))
                    {
                        throw new FetchException(FetchFailure.Forbidden);
                    }

                    uri = next; // the next connection is checked again by ConnectAsync
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new FetchException(FetchFailure.NotFound);
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new FetchException(FetchFailure.Upstream);
                }

                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!ImageTypes.Contains(type))
                {
                    throw new FetchException(FetchFailure.NotAnImage);
                }

                if (resp.Content.Headers.ContentLength > MaxBytes)
                {
                    throw new FetchException(FetchFailure.TooLarge);
                }

                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    if (ms.Length + n > MaxBytes)
                    {
                        throw new FetchException(FetchFailure.TooLarge);
                    }

                    ms.Write(buf, 0, n);
                }

                var image = new FetchedImage(ms.ToArray(), type.ToLowerInvariant());
                _cache.Set(url, image, new MemoryCacheEntryOptions { Size = image.Content.Length + 256, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
                return image;
            }
        }

        throw new FetchException(FetchFailure.Upstream);
    }

    public void Dispose()
    {
        _http.Dispose();
        _cache.Dispose();
    }
}
