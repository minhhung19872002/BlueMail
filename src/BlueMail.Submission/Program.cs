using System.Globalization;
using BlueMail.Application;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Application.Features.Sending;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Net;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using BlueMail.Submission;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// BlueMail.Submission (PROMPT 2.2): SMTP submission for mail programs, ports 587 (STARTTLS) and 465 (implicit TLS).
InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
var cfg = builder.Configuration;

int Int(string name, int fallback) => int.TryParse(cfg[name], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

builder.AddBlueMailLogging(settings, "submission");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailMailStore();
builder.Services.AddBlueMailSendingCore();
builder.Services.AddScoped<RawSubmission>();
builder.Services.AddSingleton(OidcKeys.Load(cfg));
builder.Services.AddSingleton<MailLoginService>();
var hostname = cfg["BM_SUBMISSION_HOSTNAME"] ?? "smtp." + settings.MailHostname[(settings.MailHostname.IndexOf('.', StringComparison.Ordinal) + 1)..];
builder.Services.AddSingleton(sp => new ServerCertificate(cfg, hostname, sp.GetRequiredService<ILogger<ServerCertificate>>()));
builder.Services.AddSingleton(new SubmissionOptions
{
    Hostname = hostname,
    MaxMessageBytes = Int("BM_MAX_MESSAGE_MB", 50) * 1024L * 1024,
    MaxRecipients = Int("BM_MAX_RECIPIENTS", 100),
    MaxConnections = Int("BM_SUBMISSION_MAX_CONNECTIONS", 2000),
    MaxConnectionsPerIp = Int("BM_SUBMISSION_MAX_CONNECTIONS_PER_IP", 20),
});

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(Int("BM_SUBMISSION_PORT", 587), l => l.UseConnectionHandler<StartTlsSubmission>());
    k.ListenAnyIP(Int("BM_SUBMISSIONS_PORT", 465), l => l.UseConnectionHandler<ImplicitTlsSubmission>());
    k.ListenAnyIP(Int("BM_HEALTH_PORT", 8080));
});

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.Run();
