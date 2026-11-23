using System.Text.Json;
using System.Security.Claims;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace BlueMail.Api.Realtime;

/// <summary>
/// Real-time mailbox changes (PROMPT 6.2). A client subscribes to the mailboxes it may read; the hub checks
/// access with IMailStore.OpenAsync. Payloads carry only mailbox id, modseq and kind - the client then calls
/// GET /mail/changes, so nothing private travels through the hub and a missed event is never a missed change.
/// </summary>
public sealed class MailHub(IMailStore store, ISessionValidator sessions) : Hub
{
    public static string Group(Guid mailboxId) => "mbx:" + mailboxId.ToString("N");

    /// <summary>
    /// Hub calls run outside the HTTP request pipeline (no ICurrentUser), and a connection lives for hours: the
    /// session behind the token is validated again on every subscription, so a revoked session cannot subscribe.
    /// </summary>
    public async Task<Guid> Subscribe(Guid? mailboxId)
    {
        var p = Context.User;
        if (p is null || !Guid.TryParse(p.FindFirstValue("sub"), out var userId) || !Guid.TryParse(p.FindFirstValue(OidcKeys.SessionClaim), out var sessionId)
            || p.FindFirstValue(OidcKeys.StampClaim) is not { } stamp)
        {
            throw new HubException("unauthenticated");
        }

        var state = await sessions.ValidateAsync(userId, sessionId, stamp, Context.ConnectionAborted);
        if (!state.IsValid)
        {
            Context.Abort();
            throw new HubException("unauthenticated");
        }

        try
        {
            var access = await store.OpenAsync(state.TenantId, state.UserId, mailboxId, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(access.MailboxId), Context.ConnectionAborted);
            return access.MailboxId;
        }
        catch (MailboxAccessDeniedException)
        {
            throw new HubException("mailbox_not_found");
        }
    }

    public Task Unsubscribe(Guid mailboxId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(mailboxId));
}

/// <summary>
/// Forwards Valkey mailbox events (published by every process that writes mail, e.g. BlueMail.Mx) to the hub
/// groups of this API instance. Every instance subscribes, so this is the cross-process backplane.
/// </summary>
public sealed class MailEventRelay(IConnectionMultiplexer valkey, IHubContext<MailHub> hub, ILogger<MailEventRelay> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = valkey.GetSubscriber();
        var queue = await sub.SubscribeAsync(RedisChannel.Pattern(ValkeyMailEvents.ChannelPrefix + "*"));
        stoppingToken.Register(() => queue.Unsubscribe());
        while (!stoppingToken.IsCancellationRequested)
        {
            ChannelMessage msg;
            try
            {
                msg = await queue.ReadAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
            {
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse((string)msg.Message!);
                var mailbox = doc.RootElement.GetProperty("mailboxId").GetGuid();
                var modseq = doc.RootElement.GetProperty("modseq").GetInt64();
                var kind = doc.RootElement.GetProperty("kind").GetString();
                await hub.Clients.Group(MailHub.Group(mailbox)).SendAsync("mailboxChanged", new { mailboxId = mailbox, modseq, kind }, stoppingToken);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                log.LogWarning(ex, "Ignoring malformed mailbox event on {Channel}", (string?)msg.Channel);
            }
        }
    }
}
