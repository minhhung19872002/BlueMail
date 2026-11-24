using System.Globalization;
using System.Net;
using BlueMail.MailStore;
using BlueMail.Application;
using BlueMail.Dns;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Mx;
using BlueMail.Scanner;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
var cfg = builder.Configuration;

int Int(string name, int fallback) => int.TryParse(cfg[name], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

builder.AddBlueMailLogging(settings, "mx");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailMailStore(); // not ApplicationCore: provisioning needs ASP.NET Identity (BUG-002)
builder.Services.AddBlueMailSendingCore(); // Bayes store, greylisting
builder.Services.AddScoped<BlueMail.Application.Features.Antispam.DnsblChecker>();
builder.Services.AddScoped<MxSpamFilter>();
builder.Services.AddSingleton(new MxOptions
{
    Hostname = settings.MailHostname,
    MaxMessageBytes = Int("BM_MAX_MESSAGE_MB", 50) * 1024L * 1024,
    MaxRecipients = Int("BM_MAX_RECIPIENTS", 100),
    FeedbackAddress = Environment.GetEnvironmentVariable("BM_FEEDBACK_ADDRESS") is { Length: > 0 } fbl
        ? fbl
        : "fbl@" + settings.MailHostname[(settings.MailHostname.IndexOf('.') + 1)..],
    ReportAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dmarc-reports@" + settings.MailHostname[(settings.MailHostname.IndexOf('.') + 1)..],
        "tls-reports@" + settings.MailHostname[(settings.MailHostname.IndexOf('.') + 1)..],
    },
});
builder.Services.AddSingleton(new MxListenerOptions
{
    MaxConnections = Int("BM_MX_MAX_CONNECTIONS", 2000),
    MaxConnectionsPerIp = Int("BM_MX_MAX_CONNECTIONS_PER_IP", 20),
    PreGreetDelay = TimeSpan.FromMilliseconds(Int("BM_MX_PREGREET_MS", 2000)),
});
builder.Services.AddSingleton<TlsCertificateSource>();

// Recursive resolvers: BM_DNS_SERVERS ("10.77.0.53,1.1.1.1"), otherwise the system's.
var dnsServers = (cfg["BM_DNS_SERVERS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => IPEndPoint.TryParse(s, out var ep) ? (ep.Port == 0 ? new IPEndPoint(ep.Address, 53) : ep) : null)
    .Where(e => e is not null).Select(e => e!).ToList();
if (dnsServers.Count == 0)
{
    dnsServers = SystemResolvers.Read();
}

builder.Services.AddSingleton(new DnsClient(new DnsClientOptions { Servers = dnsServers }));
builder.Services.AddSingleton<MalwareHashList>();
builder.Services.AddSingleton<PatternRuleScanner>();
builder.Services.AddHostedService<BlueMail.Application.Scanning.ScannerFeeds>();
builder.Services.AddSingleton<IFileScanner>(sp => BlueMailScanners.Create(sp.GetRequiredService<MalwareHashList>(), sp.GetRequiredService<PatternRuleScanner>()));

var smtpPort = Int("BM_MX_PORT", 25);
var healthPort = Int("BM_HEALTH_PORT", 8080);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(smtpPort, l => l.UseConnectionHandler<SmtpConnectionHandler>());
    k.ListenAnyIP(healthPort);
});

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.Run();
