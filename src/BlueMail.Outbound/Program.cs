using System.Globalization;
using System.Net;
using BlueMail.Application;
using BlueMail.Application.Features.Sending;
using BlueMail.Dns;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.MailStore;
using BlueMail.Outbound;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
var cfg = builder.Configuration;

int Int(string name, int fallback) => int.TryParse(cfg[name], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

builder.AddBlueMailLogging(settings, "outbound");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailMailStore();      // not ApplicationCore: provisioning needs ASP.NET Identity (BUG-002)
builder.Services.AddBlueMailSendingCore();

var dnsServers = (cfg["BM_DNS_SERVERS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => IPEndPoint.TryParse(s, out var ep) ? (ep.Port == 0 ? new IPEndPoint(ep.Address, 53) : ep) : null)
    .Where(e => e is not null).Select(e => e!).ToList();
if (dnsServers.Count == 0)
{
    dnsServers = File.Exists("/etc/resolv.conf")
        ? File.ReadLines("/etc/resolv.conf").Where(l => l.StartsWith("nameserver", StringComparison.Ordinal))
            .Select(l => IPAddress.TryParse(l["nameserver".Length..].Trim(), out var a) ? new IPEndPoint(a, 53) : null)
            .Where(e => e is not null).Select(e => e!).ToList()
        : throw new InvalidOperationException("BM_DNS_SERVERS is required on this platform.");
}

builder.Services.AddSingleton(new DnsClient(new DnsClientOptions { Servers = dnsServers }));
builder.Services.AddSingleton<MxResolver>();
// MTA-STS policies are fetched with certificate validation and without redirects (RFC 8461 3.3).
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
    http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueMail-MTA-STS/1.0");
    return new MtaStsResolver(sp.GetRequiredService<DnsClient>(), http);
});
builder.Services.AddSingleton(new OutboundOptions
{
    Helo = cfg["BM_OUTBOUND_HELO"] ?? settings.MailHostname,
    SmtpPort = Int("BM_OUTBOUND_PORT", 25),
    BatchSize = Int("BM_OUTBOUND_BATCH", 100),
    MaxParallelDomains = Int("BM_OUTBOUND_PARALLEL", 32),
    UseIpv6 = cfg["BM_OUTBOUND_IPV6"] == "true",
    Smarthost = string.IsNullOrWhiteSpace(cfg["BM_SMARTHOST"]) ? null : cfg["BM_SMARTHOST"],
    Dane = cfg["BM_DANE"] != "off",
});
// Extra DNSSEC trust anchors (privately signed zones, e.g. the dev doitac.test): "zone keytag alg digesttype hex; ...".
builder.Services.AddSingleton(sp => new DnssecValidator(sp.GetRequiredService<DnsClient>())
{
    ExtraAnchors = DnssecAnchors.Parse(cfg["BM_DNSSEC_ANCHORS"]),
});
builder.Services.AddSingleton<SendingIps>();
builder.Services.AddHttpClient("tlsrpt", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<TlsReportSender>();
builder.Services.AddSingleton<DeliveryWorker>();
builder.Services.AddHostedService<OutboundLoop>();
builder.Services.AddHostedService<SampleMail>(); // only with BM_SEED_SAMPLE_DATA=true

var healthPort = Int("BM_HEALTH_PORT", 8080);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(healthPort));

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.Run();

/// <summary>Submits due sends, then delivers due queue entries; sleeps briefly only when there was nothing to do.</summary>
internal sealed class OutboundLoop(OutgoingDispatcher dispatcher, DeliveryWorker worker, TlsReportSender reports, ILogger<OutboundLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReports = DateTimeOffset.UtcNow.AddMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = 0;
            try
            {
                busy += await dispatcher.RunOnceAsync(stoppingToken);
                busy += await worker.RunOnceAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextReports)
                {
                    nextReports = DateTimeOffset.UtcNow.AddHours(1); // yesterday's TLS reports go out in the first hour of the day
                    await reports.RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Outbound loop iteration failed; retrying");
            }

            if (busy == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
