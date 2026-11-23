using BlueMail.Application.Common;
using BlueMail.Application.Features.Admin.Tenants;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Application.Seeding;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Identity;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.Application;

public static class ApplicationServices
{
    /// <summary>Services every process needs: mail store, provisioning, seeding.</summary>
    public static IServiceCollection AddBlueMailApplicationCore(this IServiceCollection services)
    {
        services.AddBlueMailMailStore();
        services.AddBlueMailSendingCore();
        services.AddScoped<UserProvisioning>();
        services.AddScoped<TenantProvisioning>();
        services.AddScoped<SampleDataSeeder>();
        return services;
    }

    /// <summary>MediatR features of the web API. They need an authenticated <see cref="ICurrentUser"/>.</summary>
    public static IServiceCollection AddBlueMailApiFeatures(this IServiceCollection services)
    {
        services.AddBlueMailApplicationCore();
        services.AddScoped<Features.Mail.MailboxOpener>();
        services.AddScoped<Features.Mail.SignedContent>();
        services.AddScoped<Features.Sending.UploadService>();
        services.AddScoped<Features.Sending.LargeFiles>();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ApplicationServices).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(typeof(ApplicationServices).Assembly, includeInternalTypes: true);
        return services;
    }

    /// <summary>ASP.NET Core Identity core services shared by BlueID and the API (user management).</summary>
    public static IdentityBuilder AddBlueMailIdentityCore(this IServiceCollection services) =>
        services.AddIdentityCore<User>(o =>
            {
                o.User.AllowedUserNameCharacters = ""; // RFC 6531 addresses: any character; validated by EmailAddress.
                o.User.RequireUniqueEmail = false;     // primary_email has a unique index; aliases checked separately
                o.Password.RequiredLength = UserProvisioning.MinPasswordLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 4;
                o.Lockout.MaxFailedAccessAttempts = 1000; // the real limit is the tenant policy lockout_attempts (SignInService)
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<Role>()
            .AddUserStore<BlueMailUserStore>()
            .AddRoleStore<Microsoft.AspNetCore.Identity.EntityFrameworkCore.RoleStore<Role, BlueMailDbContext, Guid>>()
            .AddDefaultTokenProviders()
            .AddSignInManager()
            .Services.AddSingleton<ILookupNormalizer, EmailLookupNormalizer>().AddIdentityCoreBuilder();

    private static IdentityBuilder AddIdentityCoreBuilder(this IServiceCollection services) => new(typeof(User), typeof(Role), services);

    /// <summary>Drafts, sending and the dispatcher: used by the API and by BlueMail.Outbound (no ICurrentUser needed).</summary>
    public static IServiceCollection AddBlueMailSendingCore(this IServiceCollection services)
    {
        services.AddScoped<Features.Sending.DkimKeyService>();
        services.AddScoped<Features.Sending.MimeComposer>();
        services.AddScoped<Features.Sending.SenderPolicy>();
        services.AddScoped<Features.Sending.DraftService>();
        services.AddScoped<Features.Sending.SendService>();
        services.AddSingleton<Features.Sending.OutgoingDispatcher>();
        services.AddScoped<Features.Mail.SearchContexts>();
        services.AddSingleton<Features.Rules.Srs>();
        services.AddScoped<Features.Rules.SystemMail>();
        services.AddScoped<Features.Rules.MailboxRules>();
        services.AddScoped<Features.Rules.InboxOrganizer>();
        services.AddScoped<Features.Mail.SnoozeService>();
        services.AddScoped<Features.Admin.Policies.PolicyService>();
        services.AddScoped<Features.Admin.Policies.RoutingEngine>();
        services.AddScoped<Features.Admin.Directory.GroupPosting>();
        services.AddScoped<Features.Contacts.ContactRecorder>();
        services.AddScoped<Features.Rules.FilterRules>();
        services.AddScoped<Features.Antispam.BayesStore>();
        services.AddScoped<Features.Antispam.SpamLearning>();
        services.AddScoped<Features.Antispam.Greylisting>();
        services.AddScoped<Features.Antispam.PhishingReports>();
        return services;
    }
}
