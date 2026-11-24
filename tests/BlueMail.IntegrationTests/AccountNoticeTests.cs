using System.Net;
using System.Text.RegularExpressions;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>New sign-in alerts and "forgot password" (PROMPT 13 security).</summary>
[Collection(StackCollection.Name)]
public partial class AccountNoticeTests(StackFixture stack)
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();

    private async Task<HttpResponseMessage> PostForm(string userAgent, string path, Dictionary<string, string> fields)
    {
        var browser = stack.Identity.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        var page = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = Antiforgery().Match(page).Groups[1].Value;
        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private Task<HttpResponseMessage> SignIn(string email, string userAgent) =>
        PostForm(userAgent, "/account/login", new() { ["Email"] = email, ["Password"] = StackFixture.UserPassword });

    private async Task<int> Count(string to, string subject)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        return await (from m in db.Messages.IgnoreQueryFilters()
                      join b in db.Mailboxes.IgnoreQueryFilters() on m.MailboxId equals b.Id
                      join u in db.Users.IgnoreQueryFilters() on b.OwnerId equals u.Id
                      where u.PrimaryEmail == to && m.Subject == subject
                      select m.Id).CountAsync();
    }

    [Fact]
    public async Task A_sign_in_from_a_new_browser_alerts_the_owner_once()
    {
        const string email = "tomas@acme.test";
        const string subject = "New sign-in to " + email;
        (await SignIn(email, "BrowserA/1.0 (Windows)")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var afterFirst = await Count(email, subject);

        (await SignIn(email, "BrowserA/1.0 (Windows)")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await Count(email, subject)).Should().Be(afterFirst, "the same browser again is not news");

        (await SignIn(email, "BrowserB/2.0 (Android)")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await Count(email, subject)).Should().Be(afterFirst + 1, "a browser never seen before");

        // A failed attempt from yet another browser sends nothing (no session was made).
        (await PostForm("BrowserC/3.0", "/account/login", new() { ["Email"] = email, ["Password"] = "wrong-password-123" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Count(email, subject)).Should().Be(afterFirst + 1);
    }

    [Fact]
    public async Task Forgot_password_tells_the_administrators_once_and_answers_the_same_for_unknown_addresses()
    {
        const string subject = "Password reset requested for amara@acme.test";
        var before = await Count("olivia@acme.test", subject);

        var known = await PostForm("Test/1.0", "/account/forgot-password", new() { ["Email"] = "Amara@acme.test" });
        known.StatusCode.Should().Be(HttpStatusCode.OK);
        var knownText = await known.Content.ReadAsStringAsync();
        (await Count("olivia@acme.test", subject)).Should().Be(before + 1);
        (await Count("amara@acme.test", subject)).Should().Be(0, "the owner's mailbox is what they cannot open");

        await PostForm("Test/1.0", "/account/forgot-password", new() { ["Email"] = "amara@acme.test" });
        (await Count("olivia@acme.test", subject)).Should().Be(before + 1, "at most one notice per hour");

        var unknown = await PostForm("Test/1.0", "/account/forgot-password", new() { ["Email"] = "nobody-here@acme.test" });
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        Status(await unknown.Content.ReadAsStringAsync()).Should().Be(Status(knownText), "the answer must not reveal which addresses exist");
    }

    private static string Status(string html) => Regex.Match(html, "<p role=\"status\">([^<]*)</p>").Groups[1].Value;
}
