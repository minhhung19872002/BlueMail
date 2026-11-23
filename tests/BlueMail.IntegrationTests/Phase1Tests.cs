using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueMail.Application.Features.Admin.Tenants;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BlueMail.IntegrationTests;

[Collection(StackCollection.Name)]
public sealed class LoginTests(StackFixture stack)
{
    [Fact]
    public async Task Web_login_through_BlueID_gives_a_token_the_API_accepts()
    {
        var token = await OidcLogin.LoginAsync(stack, "minh@bluemail.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, token);
        var me = await api.GetFromJsonAsync<JsonElement>("/api/v1/me");
        me.GetProperty("success").GetBoolean().Should().BeTrue();
        me.GetProperty("data").GetProperty("email").GetString().Should().Be("minh@bluemail.test");
        me.GetProperty("data").GetProperty("locale").GetString().Should().Be("vi");
        me.GetProperty("data").GetProperty("timeZone").GetString().Should().Be("Asia/Ho_Chi_Minh", "user has none, tenant default applies");
    }

    [Fact]
    public async Task Wrong_password_stays_on_login_page_and_is_recorded()
    {
        var r = await OidcLogin.PostLoginAsync(stack, "hoa@bluemail.test", "wrong-password-123");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var events = await db.LoginEvents.IgnoreQueryFilters().Where(e => e.Reason == "bad_password").CountAsync();
        events.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration", "GET")]
    [InlineData("/connect/token", "POST")]
    public async Task BlueID_allows_cross_origin_calls_from_the_web_app_only(string path, string method)
    {
        using var client = stack.Identity.CreateClient();
        async Task<HttpResponseMessage> Preflight(string origin)
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, path);
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", method);
            req.Headers.Add("Access-Control-Request-Headers", "content-type");
            return await client.SendAsync(req);
        }

        var ok = await Preflight(StackFixture.WebUrl);
        ok.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
        allowed!.Single().Should().Be(StackFixture.WebUrl);

        var other = await Preflight("http://evil.test");
        other.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Api_without_token_answers_401_problem_json()
    {
        using var api = stack.Api.CreateClient();
        var r = await api.GetAsync("/api/v1/me");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("unauthenticated");
    }

    [Fact]
    public async Task Suspending_a_user_invalidates_the_token_already_held_immediately()
    {
        var victim = await OidcLogin.LoginAsync(stack, "quang@bluemail.test", StackFixture.UserPassword);
        using var victimApi = OidcLogin.ApiClient(stack, victim);
        (await victimApi.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await OidcLogin.LoginAsync(stack, "lan@bluemail.test", StackFixture.UserPassword);
        using var adminApi = OidcLogin.ApiClient(stack, admin);
        var users = await adminApi.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?search=quang");
        var id = users.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();
        var suspend = await adminApi.PutAsJsonAsync($"/api/v1/admin/users/{id}/status", new { status = "SUSPENDED" });
        suspend.StatusCode.Should().Be(HttpStatusCode.OK, await suspend.Content.ReadAsStringAsync());

        (await victimApi.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the same token must stop working at once");
        var relogin = await OidcLogin.PostLoginAsync(stack, "quang@bluemail.test", StackFixture.UserPassword);
        relogin.StatusCode.Should().Be(HttpStatusCode.OK, "a suspended user is not signed in (stays on the page with an error)");

        (await adminApi.PutAsJsonAsync($"/api/v1/admin/users/{id}/status", new { status = "ACTIVE" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Error_text_follows_the_user_language_and_keeps_a_stable_code()
    {
        var token = await OidcLogin.LoginAsync(stack, "ngoc@bluemail.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, token);
        var r = await api.GetAsync("/api/v1/admin/users");
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("permission_denied");
        body.GetProperty("title").GetString().Should().Be("Bạn không có quyền thực hiện thao tác này.");
    }
}

[Collection(StackCollection.Name)]
public sealed class TenantIsolationTests(StackFixture stack)
{
    [Fact]
    public async Task Tenant_admin_sees_only_own_tenant_users()
    {
        var admin = await OidcLogin.LoginAsync(stack, "olivia@acme.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, admin);
        var users = await api.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?pageSize=200");
        var emails = users.GetProperty("data").GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()!).ToList();
        emails.Should().NotBeEmpty().And.OnlyContain(e => e.EndsWith("@acme.test", StringComparison.Ordinal));
        users.GetProperty("data").GetProperty("total").GetInt32().Should().Be(emails.Count);
    }

    [Fact]
    public async Task Ids_of_another_tenant_answer_404_on_every_endpoint()
    {
        Guid bluemailUser, bluemailMailbox, bluemailSession;
        await using (var scope = stack.ApiScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            bluemailUser = await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == "tuan@bluemail.test").Select(u => u.Id).SingleAsync();
            bluemailMailbox = await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == bluemailUser).Select(m => m.Id).SingleAsync();
            bluemailSession = await db.Sessions.IgnoreQueryFilters().Select(s => s.Id).FirstOrDefaultAsync();
        }

        var acmeAdmin = await OidcLogin.LoginAsync(stack, "olivia@acme.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, acmeAdmin);
        (await api.PutAsJsonAsync($"/api/v1/admin/users/{bluemailUser}/status", new { status = "SUSPENDED" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.GetAsync($"/api/v1/mail/labels?mailboxId={bluemailMailbox}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.DeleteAsync($"/api/v1/account/sessions/{bluemailSession}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.GetAsync("/api/v1/admin/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "tenant admins are not platform admins");

        await using var check = stack.ApiScope();
        var db2 = check.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        (await db2.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == bluemailUser)).Status.Should().Be(Domain.Identity.UserStatus.Active);
    }

    [Fact]
    public async Task Global_filter_returns_nothing_when_no_tenant_is_set()
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        (await db.Users.CountAsync()).Should().Be(0, "closed by default");
        (await db.Users.IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Own_mailbox_has_system_labels_with_counts_from_rows()
    {
        var token = await OidcLogin.LoginAsync(stack, "jonas@acme.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, token);
        var labels = await api.GetFromJsonAsync<JsonElement>("/api/v1/mail/labels");
        var keys = labels.GetProperty("data").GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("systemKey").GetString()).ToList();
        keys.Should().Contain(SystemLabels.All);
        labels.GetProperty("data").GetProperty("usedBytes").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Platform_admin_lists_all_tenants()
    {
        await using (var scope = stack.ApiScope())
        {
            // The seeded platform admin must change the password first; clear the flag for this API-level test.
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == StackFixture.PlatformAdmin)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.MustChangePassword, false));
        }

        var token = await OidcLogin.LoginAsync(stack, StackFixture.PlatformAdmin, StackFixture.PlatformAdminPassword);
        using var api = OidcLogin.ApiClient(stack, token);
        var tenants = await api.GetFromJsonAsync<JsonElement>("/api/v1/admin/tenants");
        tenants.GetProperty("data").GetProperty("items").EnumerateArray().Select(t => t.GetProperty("code").GetString())
            .Should().Contain(["bluemail", "acme", "platform"]);
    }
}

[Collection(StackCollection.Name)]
public sealed class ConstraintTests(StackFixture stack)
{
    [Fact]
    public async Task Parallel_uid_allocation_never_duplicates()
    {
        Guid tenantId, userId, inbox;
        await using (var scope = stack.ApiScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            var u = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.PrimaryEmail == "anna@bluemail.test");
            (tenantId, userId) = (u.TenantId, u.Id);
            var mb = await db.Mailboxes.IgnoreQueryFilters().SingleAsync(m => m.OwnerId == userId);
            inbox = await db.Labels.IgnoreQueryFilters().Where(l => l.MailboxId == mb.Id && l.SystemKey == SystemLabels.Inbox).Select(l => l.Id).SingleAsync();
        }

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 40).Select(async _ =>
        {
            await using var scope = stack.ApiScope();
            var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
            var access = await store.OpenAsync(tenantId, userId, null);
            await gate.WaitAsync();
            return await store.AllocateUidsAsync(access, inbox, 1, CancellationToken.None);
        }).ToList();
        gate.Release(40);
        var uids = await Task.WhenAll(tasks);
        uids.Should().OnlyHaveUniqueItems().And.HaveCount(40);
        (uids.Max() - uids.Min()).Should().Be(39, "consecutive, no gaps");
    }

    [Fact]
    public async Task Parallel_modseq_allocation_never_duplicates()
    {
        Guid tenantId, userId;
        await using (var scope = stack.ApiScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            var u = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.PrimaryEmail == "kenji@bluemail.test");
            (tenantId, userId) = (u.TenantId, u.Id);
        }

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 40).Select(async _ =>
        {
            await using var scope = stack.ApiScope();
            var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
            var access = await store.OpenAsync(tenantId, userId, null);
            await gate.WaitAsync();
            return await store.NextModseqAsync(access, CancellationToken.None);
        }).ToList();
        gate.Release(40);
        var values = await Task.WhenAll(tasks);
        values.Should().OnlyHaveUniqueItems();
        (values.Max() - values.Min()).Should().Be(39);
    }

    [Fact]
    public async Task One_domain_belongs_to_one_tenant_even_under_parallel_creation()
    {
        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            await using var scope = stack.ApiScope();
            var tenants = scope.ServiceProvider.GetRequiredService<TenantProvisioning>();
            await gate.WaitAsync();
            try
            {
                await tenants.CreateAsync(new NewTenant($"race{i}", $"Race {i}", "race-domain.test", "en", "UTC"), CancellationToken.None);
                return "ok";
            }
            catch (Application.Common.AppException ex)
            {
                return ex.Code;
            }
        }).ToList();
        gate.Release(3);
        var results = await Task.WhenAll(tasks);
        results.Count(r => r == "ok").Should().Be(1);
        results.Count(r => r == "domain_taken").Should().Be(2);
    }

    [Fact]
    public async Task Audit_chain_stays_valid_under_parallel_writers_and_refuses_edits()
    {
        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 25).Select(async i =>
        {
            await using var scope = stack.ApiScope();
            var audit = scope.ServiceProvider.GetRequiredService<AuditWriter>();
            await gate.WaitAsync();
            await audit.WriteAsync(new AuditEntry("test.parallel", AuditResult.Success, Entity: "n", EntityId: i.ToString(),
                NewValueJson: $"{{ \"z\": {i}, \"a\": \"Tiếng Việt\" }}"));
        }).ToList();
        gate.Release(25);
        await Task.WhenAll(tasks);

        await using var verifyScope = stack.ApiScope();
        var verification = await verifyScope.ServiceProvider.GetRequiredService<AuditChainVerifier>().VerifyAsync();
        verification.Valid.Should().BeTrue();
        verification.Checked.Should().BeGreaterThanOrEqualTo(25);

        await using var conn = new NpgsqlConnection(stack.ConnectionString);
        await conn.OpenAsync();
        var update = async () => await new NpgsqlCommand("UPDATE audit.audit_logs SET action = 'forged' WHERE id = (SELECT min(id) FROM audit.audit_logs)", conn).ExecuteNonQueryAsync();
        await update.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        var delete = async () => await new NpgsqlCommand("DELETE FROM audit.audit_logs", conn).ExecuteNonQueryAsync();
        await delete.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Nul_characters_in_query_are_refused_at_the_door()
    {
        var token = await OidcLogin.LoginAsync(stack, "lan@bluemail.test", StackFixture.UserPassword);
        using var api = OidcLogin.ApiClient(stack, token);
        var r = await api.GetAsync("/api/v1/admin/users?search=a%00b");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("invalid_characters");
    }
}
