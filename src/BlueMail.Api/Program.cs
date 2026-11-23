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
var keys = OidcKeys.Load(builder.Configuration);

builder.AddBlueMailLogging(settings, "api");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailApiFeatures();
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
builder.Services.AddSingleton<BlueMail.Scanner.IFileScanner>(sp => BlueMail.Scanner.BlueMailScanners.Create(sp.GetRequiredService<BlueMail.Scanner.MalwareHashList>(), sp.GetRequiredService<BlueMail.Scanner.PatternRuleScanner>()));
builder.Services.AddSignalR().AddJsonProtocol(o => WebJson.Configure(o.PayloadSerializerOptions));
builder.Services.AddHostedService<BlueMail.Api.Realtime.MailEventRelay>();
builder.Services.AddSingleton<BlueMail.Application.Features.Rules.FilterJobWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlueMail.Application.Features.Rules.FilterJobWorker>());
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

builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
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
app.MapControllers();
app.MapHub<BlueMail.Api.Realtime.MailHub>("/hubs/mail");

app.Run();
