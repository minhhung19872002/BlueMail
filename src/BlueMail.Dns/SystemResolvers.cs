using System.Net;

namespace BlueMail.Dns;

/// <summary>Resolvers from /etc/resolv.conf (Linux containers). Other platforms must configure servers explicitly.</summary>
public static class SystemResolvers
{
    public static List<IPEndPoint> Read()
    {
        const string path = "/etc/resolv.conf";
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("BM_DNS_SERVERS is required on this platform.");
        }

        return File.ReadLines(path)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("nameserver", StringComparison.Ordinal))
            .Select(l => l["nameserver".Length..].Trim())
            .Select(s => IPAddress.TryParse(s, out var a) ? new IPEndPoint(a, 53) : null)
            .Where(e => e is not null).Select(e => e!).ToList();
    }
}
