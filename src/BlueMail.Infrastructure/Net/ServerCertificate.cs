using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlueMail.Infrastructure.Net;

/// <summary>
/// TLS certificate of a mail service (submission, IMAP): PEM files from BM_TLS_CERT / BM_TLS_KEY, reloaded when they
/// change so a renewal needs no restart (PROMPT 8.1). Without them a self-signed certificate for the host name is made;
/// mail programs will warn about it, so production always sets the files.
/// </summary>
public sealed class ServerCertificate(IConfiguration config, string hostname, ILogger<ServerCertificate> log)
{
    private readonly Lock _gate = new();
    private X509Certificate2? _cert;
    private DateTime _loadedStamp;

    public X509Certificate2 Get()
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
                    _cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
                    _loadedStamp = stamp;
                    log.LogInformation("Loaded TLS certificate {Subject}, expires {Expiry}", _cert.Subject, _cert.NotAfter);
                }

                return _cert;
            }

            if (_cert is null)
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=" + hostname, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(hostname);
                req.CertificateExtensions.Add(san.Build());
                using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pkcs12), null);
                log.LogWarning("BM_TLS_CERT/BM_TLS_KEY not set: using a self-signed certificate for {Host}", hostname);
            }

            return _cert;
        }
    }
}
