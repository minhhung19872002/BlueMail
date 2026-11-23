using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueMail.IntegrationTests.Infrastructure;

namespace BlueMail.IntegrationTests;

/// <summary>BUG-041: invalidating a user while their requests are being validated must answer 401, never 500.</summary>
[Collection(StackCollection.Name)]
public class SessionRaceTests(StackFixture stack)
{
    [Fact]
    public async Task Requests_racing_a_suspension_get_200_or_401_never_500()
    {
        using var victim = OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, "yen@bluemail.test", StackFixture.UserPassword));
        using var admin = OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, "lan@bluemail.test", StackFixture.UserPassword));
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?search=yen");
        var id = users.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetGuid();
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var requests = Enumerable.Range(0, 40).Select(_ => Task.Run(() => victim.GetAsync("/api/v1/me"))).ToList();
                var suspend = admin.PutAsJsonAsync($"/api/v1/admin/users/{id}/status", new { status = "SUSPENDED" });
                var codes = (await Task.WhenAll(requests)).Select(r => r.StatusCode).ToList();
                (await suspend).EnsureSuccessStatusCode();
                codes.Should().OnlyContain(c => c == HttpStatusCode.OK || c == HttpStatusCode.Unauthorized);
                (await victim.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await admin.PutAsJsonAsync($"/api/v1/admin/users/{id}/status", new { status = "ACTIVE" })).EnsureSuccessStatusCode();
            }
        }
        finally
        {
            await admin.PutAsJsonAsync($"/api/v1/admin/users/{id}/status", new { status = "ACTIVE" });
        }
    }
}
