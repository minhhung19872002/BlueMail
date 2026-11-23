using BlueMail.Identity.Services;
using BlueMail.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>
/// The Development environment validates the whole DI graph at start (ValidateOnBuild). A service that
/// cannot be built must fail here, not on a developer's machine (docs/08 BUG-002). Starting BlueID a second
/// time also proves migrations and seeding are idempotent.
/// </summary>
[Collection(StackCollection.Name)]
public sealed class HostStartupTests(StackFixture stack)
{
    [Fact]
    public void BlueID_starts_with_full_DI_validation_and_reseeding_is_harmless()
    {
        _ = stack;
        using var factory = new WebApplicationFactory<SignInService>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var act = () =>
        {
            using var scope = factory.Services.CreateScope(); // building the host runs ValidateOnBuild
            scope.ServiceProvider.GetRequiredService<SignInService>();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Api_starts_with_full_DI_validation()
    {
        _ = stack;
        using var factory = new WebApplicationFactory<BlueMail.Api.ApiAssemblyMarker>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var act = () =>
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<BlueMail.Application.Features.Admin.Users.UserProvisioning>();
        };
        act.Should().NotThrow();
    }
}
