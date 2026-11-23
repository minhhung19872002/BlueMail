using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using BlueMail.Application.Features.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.Application.Features.Compliance;

public static class ComplianceServiceRegistration
{
    /// <summary>
    /// Compliance (holds, retention, eDiscovery, exports) and mailbox import (Phase 10). Workers are singletons so tests
    /// can run one pass; the API registers them as hosted services.
    /// </summary>
    public static IServiceCollection AddBlueMailComplianceAndMigration(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddScoped<ComplianceScope>();
        services.AddScoped<ExportBuilder>();
        services.AddScoped<ExportDownloads>();
        services.AddScoped<MailImporter>();
        services.AddSingleton(ImportOptionsFrom(cfg));
        services.AddSingleton<ImportWorker>();
        services.AddSingleton<ComplianceWorker>();
        return services;
    }

    /// <summary>
    /// BM_IMPORT_ALLOW_PRIVATE (true only for test networks), BM_IMPORT_PORTS (comma list, default 143,993),
    /// BM_IMPORT_TRUSTED_CERTS (PEM files separated by ';', pinned certificates of test IMAP servers).
    /// </summary>
    public static ImportOptions ImportOptionsFrom(IConfiguration cfg)
    {
        var ports = (cfg["BM_IMPORT_PORTS"] ?? "143,993").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToHashSet();
        var certs = (cfg["BM_IMPORT_TRUSTED_CERTS"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => X509Certificate2.CreateFromPem(File.ReadAllText(path))).ToList();
        return new ImportOptions
        {
            AllowPrivate = string.Equals(cfg["BM_IMPORT_ALLOW_PRIVATE"], "true", StringComparison.OrdinalIgnoreCase),
            Ports = ports,
            TrustedCertificates = certs,
        };
    }
}
