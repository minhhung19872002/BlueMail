using System.Collections.Concurrent;
using System.Net;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Infrastructure.Net;
using BlueMail.MailStore;
using BlueMail.Protocols.Smtp;
using Microsoft.AspNetCore.Connections;
using StackExchange.Redis;

namespace BlueMail.Imap;

/// <summary>
/// Open sessions by user and by selected mailbox: closes a user's connections when access is revoked (the same Valkey
/// channel as the web, PROMPT 8.1) and wakes IDLE when a mailbox changes (the store's events, "bm:mbx:*").
/// </summary>
public sealed class ImapSessionRegistry
{
    private readonly ConcurrentDictionary<ImapSession, Guid> _byUser = new();
    private readonly ConcurrentDictionary<ImapSession, Guid> _watching = new();

    public ImapSessionRegistry(MailLoginService logins, IConnectionMultiplexer? valkey = null)
    {
        logins.OnUserInvalidated(user =>
        {
            foreach (var s in _byUser.Where(kv => kv.Value == user).Select(kv => kv.Key).ToList())
            {
                _ = s.RecheckAsync();
            }
        });
        valkey?.GetSubscriber().Subscribe(RedisChannel.Pattern(ValkeyMailEvents.ChannelPrefix + "*"), (channel, _) =>
        {
            var text = channel.ToString();
            if (Guid.TryParseExact(text[ValkeyMailEvents.ChannelPrefix.Length..], "N", out var mailbox))
            {
                foreach (var s in _watching.Where(kv => kv.Value == mailbox).Select(kv => kv.Key))
                {
                    s.MailboxChanged();
                }
            }
        });
    }

    internal void Add(ImapSession s, Guid user) => _byUser[s] = user;

    internal void Watch(ImapSession s, Guid mailbox) => _watching[s] = mailbox;

    internal void Unwatch(ImapSession s) => _watching.TryRemove(s, out _);

    internal void Remove(ImapSession s)
    {
        _byUser.TryRemove(s, out _);
        _watching.TryRemove(s, out _);
    }

    public int Count => _byUser.Count;
}

public sealed record ImapLimits(int MaxConnections, int MaxPerIp);

/// <summary>Port 143 (STARTTLS) or 993 (implicit TLS); global and per-IP connection limits.</summary>
public abstract class ImapConnectionHandler(IServiceProvider services, ImapServerOptions options, ServerCertificate certificate, MailLoginService logins,
    ImapSessionRegistry registry, ILogger log, bool implicitTls, ImapLimits limits) : ConnectionHandler
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
            if (total > limits.MaxConnections || mine > limits.MaxPerIp)
            {
                if (!implicitTls)
                {
                    await stream.WriteAsync("* BYE Too many connections, try later\r\n"u8.ToArray());
                }

                return;
            }

            var session = new ImapSession(stream, implicitTls, ip, services, options, certificate.Get, logins, registry, log);
            await session.RunAsync(connection.ConnectionClosed);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            PerIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
            PerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }
}

public sealed class StartTlsImap(IServiceProvider s, ImapServerOptions o, ServerCertificate c, MailLoginService l, ImapSessionRegistry r, ImapLimits lim,
    ILogger<StartTlsImap> log) : ImapConnectionHandler(s, o, c, l, r, log, implicitTls: false, lim);

public sealed class ImplicitTlsImap(IServiceProvider s, ImapServerOptions o, ServerCertificate c, MailLoginService l, ImapSessionRegistry r, ImapLimits lim,
    ILogger<ImplicitTlsImap> log) : ImapConnectionHandler(s, o, c, l, r, log, implicitTls: true, lim);
