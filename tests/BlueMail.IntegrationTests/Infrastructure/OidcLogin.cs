using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace BlueMail.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the real browser flow against BlueID: /connect/authorize -> login page (antiforgery) ->
/// authorization code -> /connect/token with PKCE. Returns an access token for the API.
/// </summary>
public static partial class OidcLogin
{
    public const string Redirect = StackFixture.WebUrl + "/auth/callback";

    public static async Task<string> LoginAsync(StackFixture stack, string email, string password)
    {
        var browser = stack.Identity.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "bluemail-web",
            ["redirect_uri"] = Redirect,
            ["response_type"] = "code",
            ["scope"] = "openid email profile offline_access bluemail-api",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "s1",
        });

        var r1 = await browser.GetAsync(authorize);
        r1.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loginUrl = r1.Headers.Location!.ToString();
        loginUrl.Should().StartWith("/account/login");

        var page = await (await browser.GetAsync(loginUrl)).Content.ReadAsStringAsync();
        var token = Antiforgery().Match(page).Groups[1].Value;
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://x" + loginUrl).Query)["returnUrl"].ToString();
        var post = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect, "sign-in with correct credentials redirects back to the authorize endpoint");

        var r2 = await browser.GetAsync(post.Headers.Location!.ToString());
        r2.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callback = r2.Headers.Location!;
        callback.ToString().Should().StartWith(Redirect);
        var code = QueryHelpers.ParseQuery(callback.Query)["code"].ToString();
        code.Should().NotBeNullOrEmpty();

        var tokenResponse = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["client_id"] = "bluemail-web",
            ["code_verifier"] = verifier,
        }));
        var json = await tokenResponse.Content.ReadAsStringAsync();
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK, json);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Posts the login form and returns the raw response (for failure-path tests).</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(StackFixture stack, string email, string password)
    {
        var browser = stack.Identity.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await (await browser.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var token = Antiforgery().Match(page).Groups[1].Value;
        return await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    public static HttpClient ApiClient(StackFixture stack, string accessToken)
    {
        var c = stack.Api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return c;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();
}
