using System.Collections.Concurrent;
using System.Net;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Net;
using BlueMail.MailStore;
using BlueMail.Protocols.Smtp;
using Microsoft.AspNetCore.Connections;

namespace BlueMail.Submission;

public sealed record SubmissionOptions
{
    public required string Hostname { get; init; }
    public long MaxMessageBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxRecipients { get; init; } = 100;
    public int MaxConnections { get; init; } = 2000;
    public int MaxConnectionsPerIp { get; init; } = 20;
}

/// <summary>
/// One submission session (RFC 6409): AUTH first (only over TLS), recipients checked against our directory, the message
/// handed to <see cref="RawSubmission"/>; 250 only once it is stored.
/// </summary>
public sealed class SubmissionHandler(IServiceProvider services, MailLoginService logins, ILogger<SubmissionHandler> log) : ISmtpHandler
{
    public MailLogin? Login { get; private set; }

    public async ValueTask<SaslResult> AuthenticateAsync(SaslCredentials c, IPAddress ip, CancellationToken ct)
    {
        var (login, failure) = await logins.AuthenticateAsync(c.User, c.Secret, c.IsBearerToken, LoginProtocol.Smtp, ip, ct);
        if (login is null)
        {
            return failure == MailLoginFailure.Locked ? SaslResult.TryLater : SaslResult.Wrong;
        }

        Login = login;
        return new SaslResult(true, login.Email);
    }

    public ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope session, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

    public ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope session, SmtpPath from, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

    public async ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope session, SmtpPath recipient, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var r = await scope.ServiceProvider.GetRequiredService<RecipientResolver>().ResolveAsync(recipient.Address, 0, ct);
        return r.Status switch
        {
            RecipientStatus.Ok or RecipientStatus.NotOurDomain => null,
            RecipientStatus.OverQuota => new SmtpReply(452, "4.2.2", "Mailbox full"),
            RecipientStatus.TenantSuspended => new SmtpReply(550, "5.2.1", "Mailbox disabled"),
            _ => new SmtpReply(550, "5.1.1", "No such user here"),
        };
    }

    public async ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope session, Stream message, CancellationToken ct)
    {
        if (Login is null || !await logins.StillValidAsync(Login, ct))
        {
            return new SmtpReply(535, "5.7.8", "Authentication no longer valid");
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            var r = await scope.ServiceProvider.GetRequiredService<RawSubmission>()
                .SubmitAsync(Login, session.Recipients.Select(p => p.Address).ToList(), message, ct);
            return new SmtpReply(r.Code, r.Enhanced, r.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Submission from {User} failed", Login.Email);
            return SmtpReply.TemporaryFailure; // nothing was promised: the program retries
        }
    }
}

/// <summary>Port 587 (STARTTLS required before AUTH) or 465 (implicit TLS, RFC 8314).</summary>
public abstract class SubmissionConnectionHandler(IServiceProvider services, SubmissionOptions options, ServerCertificate certificate, ILogger log,
    bool implicitTls) : ConnectionHandler
{
    private static int _active;
    private static readonly ConcurrentDictionary<IPAddress, int> PerIp = new();

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ip = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        await using var stream = new DuplexPipeStream(connection.Transport);
        var total = Interlocked.Increment(ref _active);
        var mine = PerIp.AddOrUpdate(ip, 1, (_, n) => n + 1);
        try
        {
            if (total > options.MaxConnections || mine > options.MaxConnectionsPerIp)
            {
                if (!implicitTls)
                {
                    await stream.WriteAsync(new SmtpReply(421, "4.7.0", options.Hostname + " too many connections, try later").ToBytes(true));
                }

                return;
            }

            var handler = ActivatorUtilities.CreateInstance<SubmissionHandler>(services);
            var session = new SmtpServerSession(new SmtpServerOptions
            {
                Hostname = options.Hostname,
                MaxMessageBytes = options.MaxMessageBytes,
                MaxRecipients = options.MaxRecipients,
                MaxCommandLineBytes = 16 * 1024, // AUTH lines carry access tokens
                StartTlsAvailable = !implicitTls,
                RequireAuthentication = true,
                Authenticate = (c, ct) => handler.AuthenticateAsync(c, ip, ct),
            }, handler, new SmtpEnvelope { RemoteAddress = ip, Tls = false });
            var transport = new SmtpTransport(stream, session, new SmtpTransportOptions
            {
                Certificate = certificate.Get,
                ImplicitTls = implicitTls,
            }, log);
            await transport.RunAsync(connection.ConnectionClosed);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            PerIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
            PerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }
}

public sealed class StartTlsSubmission(IServiceProvider s, SubmissionOptions o, ServerCertificate c, ILogger<StartTlsSubmission> l)
    : SubmissionConnectionHandler(s, o, c, l, implicitTls: false);

public sealed class ImplicitTlsSubmission(IServiceProvider s, SubmissionOptions o, ServerCertificate c, ILogger<ImplicitTlsSubmission> l)
    : SubmissionConnectionHandler(s, o, c, l, implicitTls: true);
