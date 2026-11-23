using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Domain.Identity;
using BlueMail.Imap;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.Protocols.Imap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 11: sign-in for IMAP/SMTP (passwords, app passwords, two-step verification, brute force) and revocation of open IMAP connections.</summary>
[Collection(StackCollection.Name)]
public class MailProtocolTests(StackFixture stack)
{
    private MailLoginService Logins() => new(stack.Api.Services.GetRequiredService<IServiceScopeFactory>(), stack.Api.Services.GetRequiredService<ISessionValidator>(),
        stack.Api.Services.GetRequiredService<BmSettings>(), OidcKeys.Load(stack.Api.Services.GetRequiredService<IConfiguration>()),
        stack.Api.Services.GetRequiredService<IConnectionMultiplexer>());

    private async Task<T> Db<T>(Func<BlueMailDbContext, Task<T>> f)
    {
        await using var scope = stack.ApiScope();
        return await f(scope.ServiceProvider.GetRequiredService<BlueMailDbContext>());
    }

    private async Task<(Guid User, Guid Tenant)> UserAsync(string email) =>
        await Db(async db => await db.Users.IgnoreQueryFilters().Where(u => u.PrimaryEmail == email).Select(u => new ValueTuple<Guid, Guid>(u.Id, u.TenantId)).SingleAsync());

    private async Task<Guid> AppPasswordAsync(string email, string secret)
    {
        var (user, tenant) = await UserAsync(email);
        return await Db(async db =>
        {
            var row = new AppPassword { TenantId = tenant, UserId = user, Name = "test", SecretHash = MailLoginService.HashAppPassword(secret) };
            db.AppPasswords.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
    }

    [Fact]
    public async Task Passwords_app_passwords_two_step_and_brute_force()
    {
        var logins = Logins();
        var ip = IPAddress.Parse("198.51.100." + RandomNumberGenerator.GetInt32(1, 250));
        (await logins.AuthenticateAsync("anna@bluemail.test", StackFixture.UserPassword, false, LoginProtocol.Imap, ip, default)).Login.Should().NotBeNull();
        await AppPasswordAsync("anna@bluemail.test", "abcd efgh ijkl mnop");
        (await logins.AuthenticateAsync("anna@bluemail.test", "ABCDEFGHIJKLMNOP", false, LoginProtocol.Smtp, ip, default)).Login!.AppPasswordId.Should().NotBeNull(
            "spaces and case of an app password do not matter");

        // With two-step verification on, the account password no longer works for mail programs.
        var (anna, _) = await UserAsync("anna@bluemail.test");
        await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.TwoFactorEnabled, true)));
        try
        {
            (await logins.AuthenticateAsync("anna@bluemail.test", StackFixture.UserPassword, false, LoginProtocol.Imap, ip, default)).Failure.Should().Be(MailLoginFailure.Wrong);
            (await Db(db => db.LoginEvents.IgnoreQueryFilters().Where(e => e.UserId == anna).OrderByDescending(e => e.OccurredAt).Select(e => e.Reason).FirstAsync()))
                .Should().Be("app_password_required");
            (await logins.AuthenticateAsync("anna@bluemail.test", "abcdefghijklmnop", false, LoginProtocol.Imap, ip, default)).Login.Should().NotBeNull();
        }
        finally
        {
            await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.TwoFactorEnabled, false)));
        }

        // Ten wrong passwords lock the account for a while, even the right password is not checked then.
        var guess = IPAddress.Parse("203.0.113." + RandomNumberGenerator.GetInt32(1, 250));
        for (var i = 0; i < MailLoginService.MaxFailuresPerAccount; i++)
        {
            await logins.AuthenticateAsync("thao@bluemail.test", "wrong-" + i, false, LoginProtocol.Imap, guess, default);
        }

        (await logins.AuthenticateAsync("thao@bluemail.test", StackFixture.UserPassword, false, LoginProtocol.Imap, guess, default)).Failure.Should().Be(MailLoginFailure.Locked);
        await stack.Api.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyDeleteAsync("bm:authfail:u:thao@bluemail.test");

        // IMAP access turned off by the administrator.
        await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.ImapEnabled, false)));
        (await logins.AuthenticateAsync("anna@bluemail.test", StackFixture.UserPassword, false, LoginProtocol.Imap, ip, default)).Failure.Should().Be(MailLoginFailure.Disabled);
        await Db(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.ImapEnabled, true)));
    }

    [Fact]
    public async Task Revoking_an_app_password_closes_the_open_imap_connection()
    {
        var logins = Logins();
        var registry = new ImapSessionRegistry(logins, stack.Api.Services.GetRequiredService<IConnectionMultiplexer>());
        using var rsa = RSA.Create(2048);
        using var self = new CertificateRequest("CN=imap.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pkcs12), null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var session = new ImapSession(tcp.GetStream(), implicitTls: true, IPAddress.Loopback, stack.Api.Services,
                new ImapServerOptions { Hostname = "imap.test" }, () => cert, logins, registry, NullLogger.Instance);
            await session.RunAsync(default);
        });

        var app = await AppPasswordAsync("yen@bluemail.test", "qrst uvwx yzab cdef");
        await using var client = new ImapClient(new ImapClientOptions { CertificateValidation = ImapCertificates.WithPinned([cert]) });
        await client.ConnectAsync("127.0.0.1", port, default);
        await client.LoginAsync("yen@bluemail.test", "qrstuvwxyzabcdef", default);
        var inbox = (await client.ListAsync(default)).Single(f => f.Name == "INBOX");
        (await client.ExamineAsync(inbox, default)).UidValidity.Should().BeGreaterThan(0u);

        await Db(db => db.AppPasswords.IgnoreQueryFilters().Where(a => a.Id == app).ExecuteUpdateAsync(a => a.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow)));
        await stack.Api.Services.GetRequiredService<ISessionValidator>().InvalidateUserAsync((await UserAsync("yen@bluemail.test")).User);

        (await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(server, "the server side of the session ends at once");
        var act = () => client.CapabilityAsync(default);
        await act.Should().ThrowAsync<Exception>("the connection is closed");
        listener.Stop();
    }
}
