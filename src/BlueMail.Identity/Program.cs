using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BlueMail.Application;
using BlueMail.Application.Seeding;
using BlueMail.Application.Web;
using BlueMail.Identity;
using BlueMail.Identity.Services;
using BlueMail.Infrastructure;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Serilog;

InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BmSettings.Load(builder.Configuration);
var keys = OidcKeys.Load(builder.Configuration);
var testing = builder.Environment.IsTesting();
// Plain HTTP is accepted only for local development and tests; production requires HTTPS end to end.
var plainHttpAllowed = testing || builder.Environment.IsDevelopment();

builder.AddBlueMailLogging(settings, "identity");
builder.Services.AddBlueMailInfrastructure(settings);
builder.Services.AddBlueMailApplicationCore();
builder.Services.AddBlueMailIdentityCore();
builder.Services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
builder.Services.AddSingleton<IStringLocalizer, JsonStringLocalizer>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<TenantLinks>();
builder.Services.AddDataProtection().SetApplicationName("bluemail").PersistKeysToDbContext<BlueMailDbContext>();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "bm_id";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = plainHttpAllowed ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.LoginPath = "/account/login";
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = async ctx =>
    {
        // Same session check as the API: a suspended user or revoked session is signed out of BlueID too.
        var p = ctx.Principal!;
        var ok = Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
                 && Guid.TryParse(p.FindFirstValue(OidcKeys.SessionClaim), out var sid)
                 && (await ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>()
                     .ValidateAsync(uid, sid, p.FindFirstValue("AspNet.Identity.SecurityStamp") ?? "", ctx.HttpContext.RequestAborted)).IsValid;
        if (!ok)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.ConfigureExternalCookie(o => o.Cookie.SecurePolicy = plainHttpAllowed ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);

builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<BlueMailDbContext>().ReplaceDefaultEntities<Guid>())
    .AddServer(o =>
    {
        o.SetIssuer(new Uri(settings.IdentityUrl + "/"));
        o.SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetEndSessionEndpointUris("connect/logout")
            .SetUserInfoEndpointUris("connect/userinfo");
        o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
        o.RegisterScopes("openid", "email", "profile", "offline_access", OidcKeys.ApiAudience);
        o.AddSigningKey(keys.Signing).AddEncryptionKey(keys.Encryption);
        o.DisableAccessTokenEncryption();
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15)).SetRefreshTokenLifetime(TimeSpan.FromDays(14));
        var aspnet = o.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough();
        if (plainHttpAllowed)
        {
            aspnet.DisableTransportSecurityRequirement();
        }
    });

// The SPA (another origin) reads discovery/JWKS and calls the token endpoint; only the configured web origin.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(settings.WebUrl).WithMethods("GET", "POST")
    .WithHeaders("Content-Type", "Authorization").SetPreflightMaxAge(TimeSpan.FromHours(1))));
builder.Services.AddControllers().AddJsonOptions(o => WebJson.Configure(o.JsonSerializerOptions));
builder.Services.AddRazorPages();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in settings.TrustedProxies)
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});
builder.Services.AddRateLimiter(o =>
{
    // Sign-in attempts per client IP; lockout per account is enforced by Identity on top of this.
    o.AddPolicy(IdentityRateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = testing ? 1000 : 20, Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(ctx.HttpContext, HttpStatusCode.TooManyRequests, "rate_limited"));
});

var app = builder.Build();

await StartupTasks.RunAsync(app.Services, builder.Configuration, settings);

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; form-action 'self' " +
    settings.WebUrl + "; frame-ancestors 'none'; base-uri 'none'");
app.UseMiddleware<NulQueryFilterMiddleware>();
app.UseStaticFiles();
app.UseRequestLocalization(o =>
{
    o.SetDefaultCulture("en");
    var cultures = JsonStringLocalizerFactory.Languages.Select(l => new System.Globalization.CultureInfo(l)).ToArray();
    o.SupportedCultures = cultures;
    o.SupportedUICultures = cultures;
    o.FallBackToParentCultures = true;
    // OIDC ui_locales first (BlueMail web passes the user's language), then ?culture=, cookie, Accept-Language.
    o.RequestCultureProviders.Insert(0, new Microsoft.AspNetCore.Localization.QueryStringRequestCultureProvider { QueryStringKey = "ui_locales", UIQueryStringKey = "ui_locales" });
});
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect(settings.WebUrl + "/")).AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllers();
app.MapRazorPages();

app.Run();

namespace BlueMail.Identity
{
    /// <summary>Migrations, seed data and the OIDC client registration, run once at start.</summary>
    internal static class StartupTasks
    {
        public static async Task RunAsync(IServiceProvider services, IConfiguration config, BmSettings settings)
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
            var db = sp.GetRequiredService<BlueMailDbContext>();
            if (!string.Equals(config["BM_DB_MIGRATE"], "false", StringComparison.OrdinalIgnoreCase))
            {
                // Index builds on a large messages table take minutes, not the default 30 s (BUG-072).
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(int.TryParse(config["BM_DB_MIGRATE_TIMEOUT_MINUTES"], out var m) ? m : 60));
                await db.Database.MigrateAsync();
                db.Database.SetCommandTimeout(null);
            }

            await OidcClients.EnsureAsync(sp, settings);

            var adminEmail = config["BM_PLATFORM_ADMIN_EMAIL"];
            var adminPassword = config["BM_PLATFORM_ADMIN_PASSWORD"];
            var platformDomain = config["BM_PLATFORM_DOMAIN"] ?? "bluemail.local";
            var seeder = sp.GetRequiredService<SampleDataSeeder>();
            if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPassword))
            {
                await seeder.SeedPlatformAsync(new SeedOptions(adminEmail, adminPassword, platformDomain, config["BM_SEED_USER_PASSWORD"]), CancellationToken.None);
            }
            else
            {
                log.LogWarning("BM_PLATFORM_ADMIN_EMAIL / BM_PLATFORM_ADMIN_PASSWORD not set: no platform administrator seeded");
            }

            if (string.Equals(config["BM_SEED_SAMPLE_DATA"], "true", StringComparison.OrdinalIgnoreCase))
            {
                await seeder.SeedSampleTenantsAsync(new SeedOptions(adminEmail ?? "", adminPassword ?? "", platformDomain, config["BM_SEED_USER_PASSWORD"], config["BM_DEV_DKIM_DIR"]),
                    CancellationToken.None);
            }
        }
    }
}
