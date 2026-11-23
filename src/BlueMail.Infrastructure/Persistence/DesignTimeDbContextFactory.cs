using BlueMail.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlueMail.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to build the model for migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BlueMailDbContext>
{
    public BlueMailDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("BM_DB_CONNECTION")
                 ?? "Host=localhost;Port=55432;Database=bluemail;Username=bluemail;Password=bluemail-dev";
        var builder = new DbContextOptionsBuilder<BlueMailDbContext>();
        builder.UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations", "public"))
            .UseSnakeCaseNamingConvention()
            .UseOpenIddict<Guid>();
        return new BlueMailDbContext(builder.Options, new TenantContext());
    }
}
