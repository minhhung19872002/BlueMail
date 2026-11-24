using System.Net;
using System.Threading.RateLimiting;
using BlueMail.Api.Auth;
using BlueMail.Application;
using BlueMail.Application.Web;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Serilog;

InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
// BM_ROLE: "api" serves HTTP only, "worker" runs the background jobs (same image, PROMPT 9), unset/"all" both (dev).
var role = builder.Configuration["BM_ROLE"] is { Length: > 0 } r ? r : "all";
var runJobs = role is "all" or "worker";
var keys = OidcKeys.Load(builder.Configuration);

builder.AddBlueMailLogging(settings, "api");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailApiFeatures();
BlueMail.Application.Features.Admin.Domains.DomainServiceRegistration.AddBlueMailDomainAdmin(builder.Services, builder.Configuration);
builder.Services.AddScoped<BlueMail.Application.Features.Admin.Domains.MtaStsPolicy>();
if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Admin.Domains.DomainWatchWorker>());
}

builder.Services.AddBlueMailIdentityCore();
builder.Services.AddApiAuthentication(settings, keys);
builder.Services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();

builder.Services.AddControllers()
    .AddJsonOptions(o => WebJson.Configure(o.JsonSerializerOptions));
builder.Services.Configure<ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ctx => new ObjectResult(Problems.Build(ctx.HttpContext, HttpStatusCode.BadRequest, "bad_request"))
    { StatusCode = 400, ContentTypes = { "application/problem+json" } });
builder.Services.AddSingleton<BlueMail.Scanner.MalwareHashList>();
builder.Services.AddSingleton<BlueMail.Scanner.PatternRuleScanner>();
builder.Services.AddHostedService<BlueMail.Application.Scanning.ScannerFeeds>();
builder.Services.AddSingleton<BlueMail.Application.Scanning.PublicSuffixFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Scanning.PublicSuffixFeed>());
builder.Services.AddSingleton<BlueMail.Scanner.IFileScanner>(sp => BlueMail.Scanner.BlueMailScanners.Create(sp.GetRequiredService<BlueMail.Scanner.MalwareHashList>(), sp.GetRequiredService<BlueMail.Scanner.PatternRuleScanner>()));
builder.Services.AddSignalR().AddJsonProtocol(o => WebJson.Configure(o.PayloadSerializerOptions));
builder.Services.AddHostedService<BlueMail.Api.Realtime.MailEventRelay>();
builder.Services.AddSingleton<BlueMail.Application.Features.Rules.FilterJobWorker>();
if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Rules.FilterJobWorker>());
}

builder.Services.AddSingleton<BlueMail.Application.Features.Mail.BulkMailJobWorker>();
if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Mail.BulkMailJobWorker>());
}

builder.Services.AddSingleton<BlueMail.Application.Features.Mail.MailMaintenanceWorker>();
if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Mail.MailMaintenanceWorker>());
}

BlueMail.Application.Features.Compliance.ComplianceServiceRegistration.AddBlueMailComplianceAndMigration(builder.Services, builder.Configuration);
if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Migration.ImportWorker>());
}

if (runJobs)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Compliance.ComplianceWorker>());
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "BlueMail API", Version = "v1" }));
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Client IP only from configured proxies (PROMPT 8.1), never from arbitrary X-Forwarded-For.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in settings.TrustedProxies)
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

// Requests per client IP and minute (PROMPT 8.1). Configurable: offices behind one NAT address, load tests (docs/06 P13-05).
var perMinute = int.TryParse(builder.Configuration["BM_API_RATE_PER_MINUTE"], out var rpm) && rpm > 0 ? rpm : 600;
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(ctx.HttpContext, HttpStatusCode.TooManyRequests, "rate_limited"));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();   // request logging before the exception handler (PROMPT 12)
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>("default-src 'none'; frame-ancestors 'none'");
app.UseResponseCompression();
app.UseMiddleware<NulQueryFilterMiddleware>();
app.UseRateLimiter();

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
}

app.UseAuthentication();
app.UseRequestLocalization(o =>
{
    o.SetDefaultCulture("en");
    o.SupportedCultures = CultureList.All;
    o.SupportedUICultures = CultureList.All;
    o.FallBackToParentCultures = true;
    o.RequestCultureProviders.Insert(0, new SessionCultureProvider(http =>
        SessionItem.Get(http) is { } s ? (s.State.Locale, s.State.TenantLocale) : null));
});
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
// MTA-STS policy (RFC 8461) of every verified customer domain, by Host header (mta-sts.<domain> is a CNAME to us).
app.MapGet("/.well-known/mta-sts.txt", async (HttpContext http, BlueMail.Application.Features.Admin.Domains.MtaStsPolicy policy, CancellationToken ct) =>
    await policy.ForHostAsync(http.Request.Host.Host, ct) is { } text ? Results.Text(text, "text/plain") : Results.NotFound()).AllowAnonymous();

// Mail program set-up (Thunderbird autoconfig, Outlook autodiscover): public settings, no account data.
var clientConfig = new BlueMail.Application.Features.MailAccess.MailClientConfig(app.Services.GetRequiredService<BmSettings>());
app.MapGet("/.well-known/autoconfig/mail/config-v1.1.xml", (string? emailaddress) => Results.Text(clientConfig.Thunderbird(emailaddress), "application/xml")).AllowAnonymous();
app.MapGet("/mail/config-v1.1.xml", (string? emailaddress) => Results.Text(clientConfig.Thunderbird(emailaddress), "application/xml")).AllowAnonymous();
async Task<IResult> Autodiscover(HttpContext http)
{
    // Request bodies are read asynchronously (sync IO is off) and small.
    using var body = new MemoryStream();
    var buffer = new byte[8192];
    int n;
    while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0 && body.Length < 64 * 1024)
    {
        body.Write(buffer, 0, n);
    }

    body.Position = 0;
    return Results.Text(clientConfig.Outlook(BlueMail.Application.Features.MailAccess.MailClientConfig.EmailFromOutlookRequest(body)), "application/xml");
}

app.MapPost("/autodiscover/autodiscover.xml", (Delegate)Autodiscover).AllowAnonymous(); // routes ignore case: also /Autodiscover/Autodiscover.xml
app.MapControllers();
app.MapHub<BlueMail.Api.Realtime.MailHub>("/hubs/mail");

app.Run();
