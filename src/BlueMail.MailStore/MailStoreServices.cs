using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.MailStore;

public static class MailStoreServices
{
    public static IServiceCollection AddBlueMailMailStore(this IServiceCollection services)
    {
        services.AddScoped<BlobStore>();
        services.AddScoped<UploadStore>();
        services.AddSingleton<IMailEvents, ValkeyMailEvents>();
        services.AddScoped<PgMailStore>();
        services.AddScoped<RecipientResolver>();
        services.AddScoped<IMailStore>(sp => sp.GetRequiredService<PgMailStore>());
        return services;
    }
}
