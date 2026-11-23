using System.Text.Json;
using BlueMail.Dkim;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlueMail.Application.Features.Sending;

/// <summary>
/// Submits due sends (PROMPT 2.2 steps 3-4). A send is claimed with one conditional UPDATE (UNDO_WINDOW/SCHEDULED
/// -> SUBMITTING, SKIP LOCKED), then every step is idempotent and keyed by the send's fixed Message-ID: the Sent
/// copy, each internal mailbox, and the outbound queue row (unique per outgoing id). A process killed at any
/// point leaves the row SUBMITTING; after the lease it is claimed again and only the missing steps run.
/// </summary>
public sealed class OutgoingDispatcher(IServiceScopeFactory scopes, BmSettings settings, ILogger<OutgoingDispatcher> log)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Processes due sends until none is left; returns how many were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var handled = 0;
        while (!ct.IsCancellationRequested && await ProcessOneAsync(ct))
        {
            handled++;
        }

        return handled;
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        if (await scope.ServiceProvider.GetRequiredService<PgMailStore>().ClaimDueSendAsync(Lease, ct) is not { } id)
        {
            return false;
        }

        var row = await db.Outgoing.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == id, ct);
        try
        {
            await SubmitAsync(scope.ServiceProvider, row, ct);
            await db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == id && o.Status == OutgoingStatus.Submitting)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OutgoingStatus.Submitted).SetProperty(x => x.SubmittedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        catch (ComposeException e)
        {
            // Permanent problem with the draft itself (e.g. sender lost the right to an address): stop, tell the user via status.
            log.LogWarning("Send {Id} failed: {Code}", id, e.Code);
            await db.Outgoing.IgnoreQueryFilters().Where(o => o.Id == id)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OutgoingStatus.Failed).SetProperty(x => x.Error, e.Code).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }

        // Transient errors (database, S3) propagate: the row stays SUBMITTING and is retried after the lease.
        return true;
    }

    private async Task SubmitAsync(IServiceProvider sp, Outgoing o, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var stores = sp.GetRequiredService<IMailStore>();
        var store = sp.GetRequiredService<PgMailStore>();
        var composer = sp.GetRequiredService<MimeComposer>();
        var senders = sp.GetRequiredService<SenderPolicy>();
        MailboxAccess access;
        try
        {
            access = await stores.OpenAsync(o.TenantId, o.UserId, o.MailboxId == Guid.Empty ? null : o.MailboxId, ct);
        }
        catch (MailboxAccessDeniedException)
        {
            throw new ComposeException("mailbox_gone");
        }

        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == o.TenantId && t.Status == BlueMail.Domain.Tenants.TenantStatus.Suspended, ct))
        {
            throw new ComposeException("tenant_suspended"); // a suspended tenant stops sending (PROMPT 7.1)
        }

        var draft = await db.Drafts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.Id == o.DraftId, ct) ?? throw new ComposeException("draft_not_found");
        var state = JsonSerializer.Deserialize<DraftState>(draft.State, DraftState.Json)!;
        if (!await senders.MayUseAsync(access, o.FromAddress, ct))
        {
            throw new ComposeException("from_not_allowed");
        }

        var date = o.ClaimedAt ?? o.SendAt;
        var rfcId = o.RfcMessageId.Trim('<', '>');
        // Bcc never appears in what recipients get; the Sent copy keeps it (like every mail client).
        var forRecipients = await composer.BuildAsync(access, state with { From = o.FromAddress }, rfcId, date, includeBcc: false, makeLinks: true, ct);
        var forSent = await composer.BuildAsync(access, state with { From = o.FromAddress }, rfcId, date, includeBcc: true, makeLinks: true, ct);

        // 1. Sent copy, threaded with the conversation it answers.
        if (!await HasMessageAsync(db, o.TenantId, o.MailboxId, rfcId, ct))
        {
            await DeliverRawAsync(store, o.TenantId, forSent, new MailboxDelivery(o.MailboxId, [SystemLabels.Sent], Seen: true), ct);
            // Recipients go to "Other contacts" once per send (inside the Sent-copy step, so a retried dispatch does not count twice).
            await sp.GetRequiredService<Contacts.ContactRecorder>().RecordSentAsync(o.TenantId, o.UserId,
                state.AllRecipients.Select(r => new MailboxAddress(r.Name, r.Address.Trim())), ct);
            if (o.ArchiveThread && state.ReplyToMessageId is { } replied)
            {
                var threads = await store.GetMessagesThreadsAsync(access, [replied], ct);
                await store.ApplyAsync(access, await store.ResolveMessagesAsync(access, threads, [], ct), new MessageChange { RemoveSystem = [SystemLabels.Inbox] }, ct);
            }

            // \Answered / $Forwarded go through the store: new modseq + change log, so IMAP clients see them.
            if (state.ReplyToMessageId is { } replyTo && state.Mode is "reply" or "replyAll" or "forward")
            {
                await store.MarkRepliedAsync(access, replyTo, forwarded: state.Mode == "forward", ct);
            }
        }

        // Outbound routing rules (PROMPT 7.4): REJECT stops the send, BCC adds an archive copy.
        var routingActions = await sp.GetRequiredService<BlueMail.Application.Features.Admin.Policies.RoutingEngine>().EvaluateAsync(o.TenantId, BlueMail.Domain.Tenants.RoutingDirection.Outbound,
            new BlueMail.Application.Features.Admin.Policies.RoutedMessage(o.FromAddress, state.AllRecipients.Select(r => r.Address).ToList(), state.Subject, state.Attachments.Count > 0), ct);
        if (routingActions.Any(a => a.Type == "REJECT"))
        {
            throw new ComposeException("blocked_by_policy");
        }

        var archive = routingActions.Where(a => a.Type == "BCC" && a.Address is not null).Select(a => a.Address!).ToList();

        // 2. Recipients: ours are delivered directly (PROMPT 2.2 step 3), the rest go to the outbound queue.
        var resolver = sp.GetRequiredService<RecipientResolver>();
        var external = new List<string>();
        var failed = new List<DsnRecipient>();
        foreach (var rcpt in state.AllRecipients.Select(r => r.Address.Trim()).Concat(archive).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = await resolver.ResolveAsync(rcpt, forRecipients.Length, ct);
            switch (r.Status)
            {
                case RecipientStatus.NotOurDomain:
                    external.Add(rcpt);
                    break;
                case RecipientStatus.Ok:
                    foreach (var mb in r.MailboxIds)
                    {
                        if (!await HasMessageAsync(db, r.TenantId, mb, rfcId, ct))
                        {
                            // Internal mail goes through the recipient's rules too: blocks, filters, forwarding, vacation (PROMPT 6.8).
                            var rules = sp.GetRequiredService<Rules.MailboxRules>();
                            var s = new MemoryStream(forRecipients, writable: false);
                            var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
                            var incoming = new Rules.IncomingMessage(facts, forRecipients, o.FromAddress, DateTimeOffset.UtcNow, IsSpam: false, SpamIsFinal: false);
                            var plan = await rules.PlanAsync(r.TenantId, mb, [rcpt], incoming, ct);
                            s.Position = 0;
                            var stored = await store.DeliverAsync(new DeliveryRequest(r.TenantId, s, facts, [plan.Delivery], incoming.ReceivedAt), ct);
                            await rules.AfterDeliveryAsync(r.TenantId, mb, plan, incoming, ct, stored[0].ThreadId);
                        }
                    }

                    await LogAsync(db, o, rcpt, forRecipients.Length, MailDirection.Internal, MailLogStatus.Delivered, "250 2.0.0 delivered internally", ct);
                    break;
                default:
                    var (status, text) = r.Status switch
                    {
                        RecipientStatus.OverQuota => ("5.2.2", "552 5.2.2 Mailbox full"),
                        RecipientStatus.TenantSuspended => ("5.2.1", "550 5.2.1 Mailbox disabled"),
                        _ => ("5.1.1", "550 5.1.1 No such user here"),
                    };
                    failed.Add(new DsnRecipient(rcpt, DsnAction.Failed, status, settings.MailHostname, text, DateTimeOffset.UtcNow));
                    await LogAsync(db, o, rcpt, forRecipients.Length, MailDirection.Internal, MailLogStatus.Bounced, text, ct);
                    break;
            }
        }

        // 3. Outbound queue: DKIM-signed with the From domain's key, one row per external recipient, once per send.
        if (external.Count > 0 && !await db.OutboundMessages.IgnoreQueryFilters().AnyAsync(m => m.OutgoingId == o.Id, ct))
        {
            // Mandatory footer for mail leaving the organisation (policy outbound_footer, PROMPT 7.4); internal copies stay as written.
            var policies = sp.GetRequiredService<BlueMail.Application.Features.Admin.Policies.PolicyService>();
            var footer = await policies.TextAsync(o.TenantId, await policies.OrgUnitOfAsync(o.UserId, ct), BlueMail.Application.Features.Admin.Policies.PolicyKeys.OutboundFooter, ct);
            var forExternal = string.IsNullOrWhiteSpace(footer)
                ? forRecipients
                : await composer.BuildAsync(access, state with { From = o.FromAddress, Html = state.Html + "<hr><p>" + System.Net.WebUtility.HtmlEncode(footer).Replace("\n", "<br>", StringComparison.Ordinal) + "</p>" },
                    rfcId, date, includeBcc: false, makeLinks: true, ct);
            var signed = await SignAsync(sp, o.TenantId, o.FromAddress, forExternal, ct);
            await QueueAsync(sp, o.TenantId, o.Id, o.UserId, o.FromAddress, o.RfcMessageId, signed, external, ct);
        }

        if (failed.Count > 0 && !await HasDsnAsync(db, o, ct))
        {
            var subject = state.Subject;
            var dsn = DsnBuilder.Build(settings.MailHostname, o.FromAddress, await LocaleAsync(db, o.UserId, ct), subject, rfcId, DsnBuilder.HeadersOf(forRecipients), failed,
                o.SendAt, DsnMessageId(o), DateTimeOffset.UtcNow);
            await DeliverRawAsync(store, o.TenantId, dsn, new MailboxDelivery(o.MailboxId, [SystemLabels.Inbox, SystemLabels.CategoryUpdates]), ct);
        }

        // 4. The draft is done: its stored versions leave the Drafts/Scheduled labels.
        if (draft.MessageId is { } dm)
        {
            await store.ApplyAsync(access, [dm], new MessageChange { Expunge = true }, ct);
        }

        await db.Drafts.IgnoreQueryFilters().Where(d => d.Id == draft.Id).ExecuteUpdateAsync(d => d.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow), ct);
    }

    private string DsnMessageId(Outgoing o) => $"dsn-{o.Id:N}@{settings.MailHostname}";

    private Task<bool> HasDsnAsync(BlueMailDbContext db, Outgoing o, CancellationToken ct) => HasMessageAsync(db, o.TenantId, o.MailboxId, DsnMessageId(o), ct);

    public static Task<bool> HasMessageAsync(BlueMailDbContext db, Guid tenant, Guid mailbox, string rfcId, CancellationToken ct) =>
        db.Messages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tenant && m.MailboxId == mailbox && m.RfcMessageId == rfcId && m.ExpungedAt == null && m.DeletedAt == null, ct);

    public static async Task DeliverRawAsync(PgMailStore store, Guid tenant, byte[] raw, MailboxDelivery target, CancellationToken ct)
    {
        var s = new MemoryStream(raw, writable: false);
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        s.Position = 0;
        await store.DeliverAsync(new DeliveryRequest(tenant, s, facts, [target], DateTimeOffset.UtcNow), ct);
    }

    private static async Task<string?> LocaleAsync(BlueMailDbContext db, Guid user, CancellationToken ct) =>
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == user).Select(u => u.Locale ?? db.Tenants.IgnoreQueryFilters().Where(t => t.Id == u.TenantId).Select(t => t.DefaultLocale).First()).FirstOrDefaultAsync(ct);

    internal static async Task<byte[]> SignAsync(IServiceProvider sp, Guid tenant, string from, byte[] message, CancellationToken ct)
    {
        var domain = from[(from.LastIndexOf('@') + 1)..].ToLowerInvariant();
        var key = await sp.GetRequiredService<DkimKeyService>().GetOrCreateAsync(tenant, domain, ct);
        if (key is null)
        {
            return message; // not a domain of the tenant (cannot happen for permitted From addresses)
        }

        using (key.Rsa)
        {
            var header = new DkimSigner(key.Domain, key.Selector, key.Rsa).Sign(new MemoryStream(message, writable: false));
            return [.. header, .. message];
        }
    }

    /// <summary>Signed message into the outbound queue, one row per external recipient (outgoingId null: SMTP submission).</summary>
    internal static async Task QueueAsync(IServiceProvider sp, Guid tenant, Guid? outgoingId, Guid userId, string from, string rfcMessageId, byte[] signed,
        List<string> rcpts, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var blobs = sp.GetRequiredService<BlobStore>();
        var stream = new MemoryStream(signed, writable: false);
        var sha = await BlobStore.HashAsync(stream, ct);
        if (!await blobs.ExistsAsync(tenant, sha, ct))
        {
            await blobs.PutAsync(tenant, sha, stream, ct);
        }

        var msgId = Guid.NewGuid();
        var smtpUtf8 = !System.Text.Ascii.IsValid(from) || rcpts.Any(r => !System.Text.Ascii.IsValid(r));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.outbound_messages (id, tenant_id, outgoing_id, sender_user_id, blob_sha256, size, envelope_from, dkim_domain, rfc_message_id,
                smtp_utf8, eight_bit, priority, is_notification, sender_locale, created_at)
            VALUES ({msgId}, {tenant}, {outgoingId}, {userId}, {sha}, {signed.LongLength}, {from}, {from[(from.LastIndexOf('@') + 1)..]},
                {rfcMessageId.Trim('<', '>')}, {smtpUtf8}, {true}, {0}, {false}, {await LocaleAsync(db, userId, ct)}, now())
            ON CONFLICT (outgoing_id) WHERE outgoing_id IS NOT NULL DO NOTHING
            """, ct);
        if (inserted == 1)
        {
            foreach (var r in rcpts)
            {
                var domain = r[(r.LastIndexOf('@') + 1)..].ToLowerInvariant();
                var suppressed = await db.Suppressions.AnyAsync(s => (s.TenantId == null || s.TenantId == tenant) && s.Address == r.ToLowerInvariant()
                                                                     && (s.ExpiresAt == null || s.ExpiresAt > DateTimeOffset.UtcNow), ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO mta.outbound_recipients (tenant_id, outbound_message_id, rcpt, domain, status, attempts, next_attempt_at)
                    VALUES ({tenant}, {msgId}, {r}, {domain}, {(suppressed ? "SUPPRESSED" : "QUEUED")}, {0}, now())
                    """, ct);
                await LogAsync(db, tenant, rfcMessageId, from, r, signed.Length, MailDirection.Outbound, suppressed ? MailLogStatus.Rejected : MailLogStatus.Queued,
                    suppressed ? "suppressed (earlier hard bounce or complaint)" : "queued", ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    private static Task LogAsync(BlueMailDbContext db, Outgoing o, string rcpt, long size, MailDirection dir, MailLogStatus status, string response, CancellationToken ct) =>
        LogAsync(db, o.TenantId, o.RfcMessageId, o.FromAddress, rcpt, size, dir, status, response, ct);

    internal static async Task LogAsync(BlueMailDbContext db, Guid tenant, string rfcMessageId, string from, string rcpt, long size, MailDirection dir, MailLogStatus status,
        string response, CancellationToken ct)
    {
        db.MailLog.Add(new MailLogEntry
        {
            TenantId = tenant, Direction = dir, RfcMessageId = rfcMessageId.Trim('<', '>'), EnvelopeFrom = from, Rcpt = rcpt, Size = size,
            Status = status, SmtpResponse = response, OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
