using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Security;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace BlueMail.Identity.Services;

/// <summary>Registers (or updates) the first-party web client. Public client: authorization code + PKCE.</summary>
internal static class OidcClients
{
    public static async Task EnsureAsync(IServiceProvider sp, BmSettings settings)
    {
        var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
        var scopes = sp.GetRequiredService<IOpenIddictScopeManager>();

        if (await scopes.FindByNameAsync(OidcKeys.ApiAudience) is null)
        {
            await scopes.CreateAsync(new OpenIddictScopeDescriptor { Name = OidcKeys.ApiAudience, Resources = { OidcKeys.ApiAudience } });
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = OidcKeys.WebClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "BlueMail Web",
            RedirectUris = { new Uri(settings.WebUrl + "/auth/callback") },
            PostLogoutRedirectUris = { new Uri(settings.WebUrl + "/") },
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Prefixes.Scope + OidcKeys.ApiAudience,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        var existing = await apps.FindByClientIdAsync(OidcKeys.WebClientId);
        if (existing is null)
        {
            await apps.CreateAsync(descriptor);
        }
        else
        {
            await apps.UpdateAsync(existing, descriptor);
        }
    }
}
