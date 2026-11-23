using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlueMail.Protocols.Smtp;
using Microsoft.AspNetCore.Connections;

namespace BlueMail.Mx;

public sealed record MxListenerOptions
{
    public int MaxConnections { get; init; } = 2000;
    public int MaxConnectionsPerIp { get; init; } = 20;
    public TimeSpan PreGreetDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Kestrel connection handler: one SMTP session per TCP connection, with global and per-IP limits.</summary>
public sealed class SmtpConnectionHandler(
    IServiceProvider services,
    MxOptions mx,
    MxListenerOptions limits,
    TlsCertificateSource certificates,
    ILogger<SmtpConnectionHandler> log) : ConnectionHandler
{
    private static int _active;
    private static readonly ConcurrentDictionary<IPAddress, int> PerIp = new();

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ip = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        await using var stream = new DuplexPipeStream(connection.Transport);
        var total = Interlocked.Increment(ref _active);
        var mine = PerIp.AddOrUpdate(ip, 1, (_, n) => n + 1);
        try
        {
            if (total > limits.MaxConnections || mine > limits.MaxConnectionsPerIp)
            {
                await stream.WriteAsync(new SmtpReply(421, "4.7.0", mx.Hostname + " too many connections, try later").ToBytes(true));
                await stream.FlushAsync();
                return;
            }

            var handler = ActivatorUtilities.CreateInstance<MxHandler>(services);
            var session = new SmtpServerSession(new SmtpServerOptions
            {
                Hostname = mx.Hostname,
                MaxMessageBytes = mx.MaxMessageBytes,
                MaxRecipients = mx.MaxRecipients,
                StartTlsAvailable = true,
            }, handler, new SmtpEnvelope { RemoteAddress = ip });
            var transport = new SmtpTransport(stream, session, new SmtpTransportOptions
            {
                Certificate = certificates.Get,
                PreGreetDelay = limits.PreGreetDelay,
            }, log);
            await transport.RunAsync(connection.ConnectionClosed);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            PerIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
            PerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }
}

/// <summary>
/// Certificate for STARTTLS: PEM files from BM_TLS_CERT / BM_TLS_KEY, reloaded when they change (Caddy renews them).
/// Without them a self-signed certificate is generated: opportunistic TLS between MTAs does not validate
/// certificates (RFC 7435), so encryption still works; MTA-STS needs a real certificate (docs/03).
/// </summary>
public sealed class TlsCertificateSource(IConfiguration config, MxOptions mx, ILogger<TlsCertificateSource> log)
{
    private readonly Lock _gate = new();
    private X509Certificate2? _cert;
    private DateTime _loadedStamp;

    public X509Certificate2? Get()
    {
        var certPath = config["BM_TLS_CERT"];
        var keyPath = config["BM_TLS_KEY"];
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(certPath) && !string.IsNullOrEmpty(keyPath) && File.Exists(certPath) && File.Exists(keyPath))
            {
                var stamp = File.GetLastWriteTimeUtc(certPath);
                if (_cert is null || stamp != _loadedStamp)
                {
                    using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    _cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null); // SslStream on Linux/Windows needs an exportable key
                    _loadedStamp = stamp;
                    log.LogInformation("Loaded TLS certificate {Subject}, expires {Expiry}", _cert.Subject, _cert.NotAfter);
                }

                return _cert;
            }

            if (_cert is null)
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=" + mx.Hostname, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(mx.Hostname);
                req.CertificateExtensions.Add(san.Build());
                using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pkcs12), null);
                log.LogWarning("BM_TLS_CERT/BM_TLS_KEY not set: STARTTLS uses a self-signed certificate for {Host}", mx.Hostname);
            }

            return _cert;
        }
    }
}
