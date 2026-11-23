using System.Globalization;
using System.Text;
using BlueMail.Application.Features.Admin.Policies;
using BlueMail.Application.Features.MailAccess;
using BlueMail.Domain.Admin;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.Application.Features.Sending;

/// <summary>The answer to give the SMTP client: positive only after everything is durably stored (PROMPT 2.1 step 8).</summary>
public sealed record SubmissionReply(int Code, string Enhanced, string Text)
{
    public static SubmissionReply Accepted(string id) => new(250, "2.0.0", "Accepted as <" + id + ">");
}

/// <summary>
/// Mail sent by a mail program through SMTP submission (PROMPT 2.2, RFC 6409): the same checks and steps as the web
/// dispatcher - From must be an address the user may use, tenant policies, a Sent copy, direct delivery (with the
/// recipient's rules) for our recipients, DKIM-signed queue rows for the others. Message-ID and Date are added when the
/// program left them out; Bcc never reaches recipients.
/// </summary>
public sealed class RawSubmission(IServiceProvider sp, BlueMailDbContext db, PgMailStore store, IMailStore stores, SenderPolicy senders, PolicyService policies,
    RecipientResolver resolver, BmSettings settings)
{
    public async Task<SubmissionReply> SubmitAsync(MailLogin login, IReadOnlyList<string> recipients, Stream message, CancellationToken ct)
    {
        message.Position = 0;
        var raw = new byte[message.Length];
        await message.ReadExactlyAsync(raw, ct);
        var parsed = MimeMessage.Parse(raw);
        if (parsed.From.Count != 1)
        {
            return new SubmissionReply(550, "5.6.0", "The message needs exactly one From address");
        }

        var from = parsed.From[0].Address.Trim();
        MailboxAccess access;
        try
        {
            access = await stores.OpenAsync(login.TenantId, login.UserId, null, ct);
        }
        catch (MailboxAccessDeniedException)
        {
            return new SubmissionReply(550, "5.7.1", "No mailbox for this account");
        }

        if (!await senders.MayUseAsync(access, from, ct))
        {
            return new SubmissionReply(550, "5.7.1", "Not allowed to send as " + from);
        }

        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == login.TenantId && t.Status == BlueMail.Domain.Tenants.TenantStatus.Suspended, ct))
        {
            return new SubmissionReply(554, "5.7.1", "Sending is suspended for this organisation");
        }

        // Tenant policies (PROMPT 7.4): message size, blocked file types.
        var unit = await policies.OrgUnitOfAsync(login.UserId, ct);
        var maxMb = await policies.IntAsync(login.TenantId, unit, PolicyKeys.MaxMessageSizeMb, ct);
        if (raw.LongLength > maxMb * 1024L * 1024)
        {
            return new SubmissionReply(552, "5.3.4", "Message larger than the organisation allows");
        }

        var blocked = await policies.ListAsync(login.TenantId, unit, PolicyKeys.BlockedExtensions, ct);
        var names = parsed.Root.Walk().Where(p => p.FileName is not null).Select(p => p.FileName!);
        if (names.Any(n => blocked.Any(b => n.EndsWith("." + b.TrimStart('.'), StringComparison.OrdinalIgnoreCase))))
        {
            return new SubmissionReply(550, "5.7.1", "Attachment type not allowed by the organisation");
        }

        // Message-ID and Date (RFC 6409 8.3): added by the MSA when missing.
        var rfcId = parsed.MessageId?.Trim('<', '>');
        var extra = new StringBuilder();
        if (string.IsNullOrEmpty(rfcId))
        {
            rfcId = Guid.NewGuid().ToString("N") + "@" + settings.MailHostname;
            extra.Append("Message-ID: <").Append(rfcId).Append(">\r\n");
        }

        if (parsed.Headers["Date"] is null)
        {
            extra.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        var forSent = extra.Length == 0 ? raw : [.. Encoding.ASCII.GetBytes(extra.ToString()), .. raw];
        var forRecipients = WithoutHeader(forSent, "Bcc");

        // 1. Sent copy (once: a retried submission or the client's own IMAP APPEND to Sent does not duplicate it).
        if (!await OutgoingDispatcher.HasMessageAsync(db, login.TenantId, access.MailboxId, rfcId, ct))
        {
            await OutgoingDispatcher.DeliverRawAsync(store, login.TenantId, forSent, new MailboxDelivery(access.MailboxId, [SystemLabels.Sent], Seen: true), ct);
            await sp.GetRequiredService<Contacts.ContactRecorder>().RecordSentAsync(login.TenantId, login.UserId,
                parsed.To.Concat(parsed.Cc).Concat(parsed.Bcc).Select(a => new MailboxAddress(a.Name, a.Address)), ct);
        }

        var routing = await sp.GetRequiredService<RoutingEngine>().EvaluateAsync(login.TenantId, BlueMail.Domain.Tenants.RoutingDirection.Outbound,
            new RoutedMessage(from, recipients, parsed.Subject, parsed.Root.Walk().Any(p => p.IsAttachment)), ct);
        if (routing.Any(a => a.Type == "REJECT"))
        {
            return new SubmissionReply(550, "5.7.1", "Blocked by the organisation's policy");
        }

        // 2. Our recipients get it directly, through their own rules; the rest go to the outbound queue.
        var external = new List<string>();
        foreach (var rcpt in recipients.Concat(routing.Where(a => a.Type == "BCC" && a.Address is not null).Select(a => a.Address!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = await resolver.ResolveAsync(rcpt, forRecipients.Length, ct);
            if (r.Status == RecipientStatus.NotOurDomain)
            {
                external.Add(rcpt);
                continue;
            }

            if (r.Status != RecipientStatus.Ok)
            {
                continue; // refused at RCPT already; a race (user deleted meanwhile) loses only this copy
            }

            foreach (var mb in r.MailboxIds.Where(mb => mb != access.MailboxId))
            {
                if (await OutgoingDispatcher.HasMessageAsync(db, r.TenantId, mb, rfcId, ct))
                {
                    continue;
                }

                var rules = sp.GetRequiredService<Rules.MailboxRules>();
                var s = new MemoryStream(forRecipients, writable: false);
                var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
                var incoming = new Rules.IncomingMessage(facts, forRecipients, from, DateTimeOffset.UtcNow, IsSpam: false, SpamIsFinal: false);
                var plan = await rules.PlanAsync(r.TenantId, mb, [rcpt], incoming, ct);
                s.Position = 0;
                var stored = await store.DeliverAsync(new DeliveryRequest(r.TenantId, s, facts, [plan.Delivery], incoming.ReceivedAt), ct);
                await rules.AfterDeliveryAsync(r.TenantId, mb, plan, incoming, ct, stored[0].ThreadId);
            }

            await OutgoingDispatcher.LogAsync(db, login.TenantId, rfcId, from, rcpt, forRecipients.Length, MailDirection.Internal, MailLogStatus.Delivered,
                "250 2.0.0 delivered internally", ct);
        }

        if (external.Count > 0 && !await db.OutboundMessages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == login.TenantId && m.RfcMessageId == rfcId
                                                                                               && m.SenderUserId == login.UserId, ct))
        {
            var footer = await policies.TextAsync(login.TenantId, unit, PolicyKeys.OutboundFooter, ct);
            var outgoing = string.IsNullOrWhiteSpace(footer) ? forRecipients : WithFooter(forRecipients, footer);
            var signed = await OutgoingDispatcher.SignAsync(sp, login.TenantId, from, outgoing, ct);
            await OutgoingDispatcher.QueueAsync(sp, login.TenantId, null, login.UserId, from, rfcId, signed, external, ct);
        }

        return SubmissionReply.Accepted(rfcId);
    }

    /// <summary>The header block without every occurrence of a field (folded lines included); the body untouched.</summary>
    public static byte[] WithoutHeader(byte[] raw, string field)
    {
        var text = Encoding.Latin1.GetString(raw);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0)
        {
            return raw;
        }

        var lines = text[..(end + 2)].Split("\r\n");
        var sb = new StringBuilder();
        var skipping = false;
        foreach (var line in lines.Take(lines.Length - 1))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                if (!skipping)
                {
                    sb.Append(line).Append("\r\n");
                }

                continue;
            }

            skipping = line.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase);
            if (!skipping)
            {
                sb.Append(line).Append("\r\n");
            }
        }

        return Encoding.Latin1.GetBytes(sb + text[(end + 2)..]);
    }

    /// <summary>
    /// The mandatory footer for mail leaving the organisation (policy outbound_footer) on an arbitrary MIME message: the
    /// original body becomes the first part of a multipart/mixed, the footer a text part after it.
    /// </summary>
    public static byte[] WithFooter(byte[] raw, string footer)
    {
        var text = Encoding.Latin1.GetString(raw);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0)
        {
            return raw;
        }

        var outer = new StringBuilder();
        var inner = new StringBuilder();
        var current = outer;
        foreach (var line in text[..end].Split("\r\n"))
        {
            if (line.Length == 0 || line[0] is not (' ' or '\t'))
            {
                current = line.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ? inner
                    : line.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase) ? null! : outer;
            }

            current?.Append(line).Append("\r\n");
        }

        if (inner.Length == 0)
        {
            inner.Append("Content-Type: text/plain; charset=us-ascii\r\n");
        }

        var boundary = "=_bm_footer_" + Guid.NewGuid().ToString("N");
        var footerB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(footer + "\r\n"), Base64FormattingOptions.InsertLineBreaks);
        var result = outer + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"" + boundary + "\"\r\n\r\n"
                     + "--" + boundary + "\r\n" + inner + "\r\n" + text[(end + 4)..].TrimEnd('\r', '\n') + "\r\n"
                     + "--" + boundary + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: inline\r\n\r\n"
                     + footerB64 + "\r\n--" + boundary + "--\r\n";
        return Encoding.Latin1.GetBytes(result);
    }
}
