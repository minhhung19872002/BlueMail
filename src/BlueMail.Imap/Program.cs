using System.Globalization;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Imap;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Net;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// BlueMail.Imap (PROMPT 13, phase 11): IMAP for mail programs, ports 143 (STARTTLS) and 993 (implicit TLS).
InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
var cfg = builder.Configuration;

int Int(string name, int fallback) => int.TryParse(cfg[name], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

builder.AddBlueMailLogging(settings, "imap");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailMailStore();
builder.Services.AddSingleton(OidcKeys.Load(cfg));
builder.Services.AddSingleton<MailLoginService>();
builder.Services.AddSingleton<ImapSessionRegistry>();
var hostname = cfg["BM_IMAP_HOSTNAME"] ?? "imap." + settings.MailHostname[(settings.MailHostname.IndexOf('.', StringComparison.Ordinal) + 1)..];
builder.Services.AddSingleton(sp => new ServerCertificate(cfg, hostname, sp.GetRequiredService<ILogger<ServerCertificate>>()));
builder.Services.AddSingleton(new ImapServerOptions
{
    Hostname = hostname,
    MaxAppendBytes = Int("BM_MAX_MESSAGE_MB", 50) * 1024L * 1024,
    Trace = cfg["BM_IMAP_TRACE"] is "1" or "true",
});
builder.Services.AddSingleton(new ImapLimits(Int("BM_IMAP_MAX_CONNECTIONS", 5000), Int("BM_IMAP_MAX_CONNECTIONS_PER_IP", 50)));

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(Int("BM_IMAP_PORT", 143), l => l.UseConnectionHandler<StartTlsImap>());
    k.ListenAnyIP(Int("BM_IMAPS_PORT", 993), l => l.UseConnectionHandler<ImplicitTlsImap>());
    k.ListenAnyIP(Int("BM_HEALTH_PORT", 8080));
});

var app = builder.Build();
_ = app.Services.GetRequiredService<ImapSessionRegistry>(); // subscribe before the first connection
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.Run();
