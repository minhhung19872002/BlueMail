using BlueMail.Application.Features.Sending;
using BlueMail.Dkim;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Rules;

/// <summary>
/// Mail BlueMail itself sends on a user's behalf (forwards, auto replies, verification codes): internal recipients
/// are delivered directly, external ones go to the outbound queue (BlueMail.Outbound delivers, retries, bounces).
/// </summary>
public sealed class SystemMail(BlueMailDbContext db, PgMailStore store, BlobStore blobs, RecipientResolver resolver, DkimKeyService dkim)
{
    /// <summary>Adds a DKIM signature of <paramref name="domain"/> when the tenant has a key for it.</summary>
    public async Task<byte[]> SignAsync(Guid tenant, string domain, byte[] message, CancellationToken ct)
    {
        var key = await dkim.GetOrCreateAsync(tenant, domain.ToLowerInvariant(), ct);
        if (key is null)
        {
            return message;
        }

        using (key.Rsa)
        {
            return [.. new DkimSigner(key.Domain, key.Selector, key.Rsa).Sign(new MemoryStream(message, writable: false)), .. message];
        }
    }

    /// <summary>ARC set (RFC 8617) of <paramref name="domain"/> over a message we pass on, recording what we verified.</summary>
    public async Task<byte[]> ArcSealAsync(Guid tenant, string domain, byte[] message, string authServId, string authResults, ArcStatus chain, CancellationToken ct)
    {
        var key = await dkim.GetOrCreateAsync(tenant, domain.ToLowerInvariant(), ct);
        if (key is null || chain == ArcStatus.Fail)
        {
            return message; // a broken chain is not extended (RFC 8617 5.1.2)
        }

        using (key.Rsa)
        {
            return [.. new ArcSigner(key.Domain, key.Selector, key.Rsa).Sign(new MemoryStream(message, writable: false), authServId, authResults, chain), .. message];
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="rcpt"/>. <paramref name="envelopeFrom"/> empty = null sender
    /// (no bounce comes back). Returns false when the recipient is internal but cannot receive.
    /// </summary>
    public async Task<bool> SendAsync(Guid tenant, Guid? senderUserId, string envelopeFrom, string rcpt, byte[] message, bool notification, string? locale,
        string? rfcMessageId, CancellationToken ct)
    {
        var r = await resolver.ResolveAsync(rcpt, message.Length, ct);
        if (r.Status == RecipientStatus.Ok)
        {
            foreach (var mb in r.MailboxIds)
            {
                await OutgoingDispatcher.DeliverRawAsync(store, r.TenantId, message, new MailboxDelivery(mb, [SystemLabels.Inbox, SystemLabels.CategoryPrimary],
                    DeliveredTo: [rcpt]), ct);
            }

            return true;
        }

        if (r.Status != RecipientStatus.NotOurDomain)
        {
            return false;
        }

        var stream = new MemoryStream(message, writable: false);
        var sha = await BlobStore.HashAsync(stream, ct);
        if (!await blobs.ExistsAsync(tenant, sha, ct))
        {
            await blobs.PutAsync(tenant, sha, stream, ct);
        }

        var id = Guid.NewGuid();
        var domain = rcpt[(rcpt.LastIndexOf('@') + 1)..].ToLowerInvariant();
        var dkimDomain = envelopeFrom.Contains('@', StringComparison.Ordinal) ? envelopeFrom[(envelopeFrom.LastIndexOf('@') + 1)..] : null;
        var utf8 = !System.Text.Ascii.IsValid(envelopeFrom) || !System.Text.Ascii.IsValid(rcpt);
        var suppressed = await db.Suppressions.AnyAsync(s => (s.TenantId == null || s.TenantId == tenant) && s.Address == rcpt.ToLowerInvariant()
                                                             && (s.ExpiresAt == null || s.ExpiresAt > DateTimeOffset.UtcNow), ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.outbound_messages (id, tenant_id, outgoing_id, sender_user_id, blob_sha256, size, envelope_from, dkim_domain, rfc_message_id,
                smtp_utf8, eight_bit, priority, is_notification, sender_locale, created_at)
            VALUES ({id}, {tenant}, {null}, {senderUserId}, {sha}, {message.LongLength}, {envelopeFrom}, {dkimDomain}, {rfcMessageId?.Trim('<', '>')},
                {utf8}, {true}, {0}, {notification}, {locale}, now())
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mta.outbound_recipients (tenant_id, outbound_message_id, rcpt, domain, status, attempts, next_attempt_at)
            VALUES ({tenant}, {id}, {rcpt}, {domain}, {(suppressed ? "SUPPRESSED" : "QUEUED")}, {0}, now())
            """, ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
