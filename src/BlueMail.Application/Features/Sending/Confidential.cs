using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Sending;

/// <summary>Confidential mode chosen in the compose window: link lifetime and whether a one-time code is needed.</summary>
public sealed record DraftConfidential(int ExpiresDays, bool RequireCode)
{
    public static readonly int[] AllowedDays = [1, 7, 30, 90, 365];
}

public sealed record ConfidentialMetaDto(string Subject, string From, DateTimeOffset ExpiresAt, bool RequireCode, string Recipient);

public sealed record ConfidentialFileDto(string Name, string ContentType, long Size, string Url);

public sealed record ConfidentialContentDto(string Subject, string From, DateTimeOffset Date, DateTimeOffset ExpiresAt, string? HtmlDocument, string? Text,
    IReadOnlyList<ConfidentialFileDto> Files);

public sealed record ConfidentialRecipientDto(string Address, int Opens, DateTimeOffset? FirstOpenedAt);

public sealed record ConfidentialStatusDto(Guid Id, DateTimeOffset ExpiresAt, bool RequireCode, DateTimeOffset? RevokedAt, IReadOnlyList<ConfidentialRecipientDto> Recipients);

/// <summary>
/// Confidential mode (PROMPT 6.4, Phase 12). External recipients get a mail with a personal link instead of the
/// message; the link opens a BlueMail page that shows the message until it expires or the sender revokes it,
/// optionally after a 6-digit code mailed to that recipient. Link tokens are derived (HMAC) so a retried dispatch
/// produces the same links; only their hashes are stored. Internal recipients get the message itself (they are
/// inside the same system). Nothing stops a reader from copying or photographing what is on the screen.
/// </summary>
public sealed class ConfidentialService(IServiceProvider sp, BlueMailDbContext db, BlobStore blobs, SystemMail systemMail, BmSettings settings)
{
    public const int MaxCodeAttempts = 5;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FileLinkLifetime = TimeSpan.FromMinutes(30);
    private readonly byte[] _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(settings.MasterKeyBase64), 32,
        info: Encoding.ASCII.GetBytes("bluemail-confidential-v1"));

    /// <summary>Stores the message and mails each external recipient their link (idempotent per send).</summary>
    public async Task SendLinksAsync(Outgoing o, DraftConfidential mode, string subject, byte[] content, IReadOnlyList<string> external, string? senderName,
        string? senderLocale, CancellationToken ct)
    {
        var rfcId = o.RfcMessageId.Trim('<', '>');
        var msg = await db.ConfidentialMessages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.TenantId == o.TenantId && m.RfcMessageId == rfcId && m.DeletedAt == null, ct);
        if (msg is null)
        {
            var stream = new MemoryStream(content, writable: false);
            var sha = await BlobStore.HashAsync(stream, ct);
            if (!await blobs.ExistsAsync(o.TenantId, sha, ct))
            {
                await blobs.PutAsync(o.TenantId, sha, stream, ct);
            }

            var days = DraftConfidential.AllowedDays.Contains(mode.ExpiresDays) ? mode.ExpiresDays : 7;
            msg = new ConfidentialMessage
            {
                Id = Guid.NewGuid(), TenantId = o.TenantId, SenderUserId = o.UserId, RfcMessageId = rfcId, Subject = Truncate(subject, 998), FromAddress = o.FromAddress,
                BlobSha256 = sha, ExpiresAt = DateTimeOffset.UtcNow.AddDays(days), RequireCode = mode.RequireCode,
            };
            db.ConfidentialMessages.Add(msg);
            await db.SaveChangesAsync(ct);
        }

        foreach (var address in external.Select(a => a.Trim().ToLowerInvariant()).Distinct())
        {
            var token = TokenFor(msg.Id, address);
            var notice = $"conf-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16]}-{rfcId}";
            if (!await db.ConfidentialRecipients.IgnoreQueryFilters().AnyAsync(r => r.ConfidentialMessageId == msg.Id && r.Address == address && r.DeletedAt == null, ct))
            {
                db.ConfidentialRecipients.Add(new ConfidentialRecipient
                {
                    Id = Guid.NewGuid(), TenantId = o.TenantId, ConfidentialMessageId = msg.Id, Address = address, TokenHash = Hash(token),
                });
                await db.SaveChangesAsync(ct);
            }

            if (await db.OutboundMessages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == o.TenantId && m.RfcMessageId == notice, ct))
            {
                continue; // this recipient's link mail is already queued
            }

            var sender = string.IsNullOrWhiteSpace(senderName) ? o.FromAddress : $"{senderName} <{o.FromAddress}>";
            var args = new Dictionary<string, object?> { ["sender"] = sender, ["link"] = settings.WebUrl + "/c/" + token, ["expires"] = msg.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) };
            var (subj, body) = Texts("confidential.mail", senderLocale, args);
            var mail = new MessageBuilder
            {
                From = new MailboxAddress(senderName, o.FromAddress),
                To = [new MailboxAddress(null, address)],
                Subject = subj,
                Date = DateTimeOffset.UtcNow,
                MessageId = notice,
                TextBody = body,
                ExtraHeaders = [("Auto-Submitted", "auto-generated")],
            }.ToBytes();
            var signed = await OutgoingDispatcher.SignAsync(sp, o.TenantId, o.FromAddress, mail, ct);
            await OutgoingDispatcher.QueueAsync(sp, o.TenantId, null, o.UserId, o.FromAddress, notice, signed, [address], ct);
        }
    }

    public string TokenFor(Guid message, string address) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, (byte[])[.. message.ToByteArray(), .. Encoding.UTF8.GetBytes(address.ToLowerInvariant())]))[..32]
            .Replace('+', '-').Replace('/', '_');

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private async Task<(ConfidentialMessage Msg, ConfidentialRecipient Rcpt)> FindAsync(string token, CancellationToken ct)
    {
        if (token.Length != 32)
        {
            throw AppException.NotFound("confidential_not_found");
        }

        var hash = Hash(token);
        var rcpt = await db.ConfidentialRecipients.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TokenHash == hash && r.DeletedAt == null, ct)
                   ?? throw AppException.NotFound("confidential_not_found");
        var msg = await db.ConfidentialMessages.IgnoreQueryFilters().FirstAsync(m => m.Id == rcpt.ConfidentialMessageId, ct);
        if (msg.DeletedAt is not null || msg.RevokedAt is not null)
        {
            throw AppException.Forbidden("confidential_revoked");
        }

        if (msg.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw AppException.Forbidden("confidential_expired");
        }

        return (msg, rcpt);
    }

    public async Task<ConfidentialMetaDto> MetaAsync(string token, CancellationToken ct)
    {
        var (msg, rcpt) = await FindAsync(token, ct);
        return new ConfidentialMetaDto(msg.Subject, msg.FromAddress, msg.ExpiresAt, msg.RequireCode, Mask(rcpt.Address));
    }

    /// <summary>"lan.nguyen@example.com" -> "la•••@example.com": enough for the reader to recognise the address.</summary>
    private static string Mask(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 2 ? address : address[..2] + "•••" + address[at..];
    }

    /// <summary>Mails a new one-time code to the recipient (at most one a minute).</summary>
    public async Task SendCodeAsync(string token, CancellationToken ct)
    {
        var (msg, rcpt) = await FindAsync(token, ct);
        if (!msg.RequireCode)
        {
            return;
        }

        if (rcpt.CodeExpiresAt is { } exp && exp - CodeLifetime > DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            throw AppException.Conflict("confidential_code_wait");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        rcpt.CodeHash = Hash(token + ":" + code);
        rcpt.CodeExpiresAt = DateTimeOffset.UtcNow + CodeLifetime;
        rcpt.CodeAttempts = 0;
        await db.SaveChangesAsync(ct);

        var locale = await db.Users.IgnoreQueryFilters().Where(u => u.Id == msg.SenderUserId).Select(u => u.Locale).FirstOrDefaultAsync(ct);
        var (subject, body) = Texts("confidential.code", locale, new Dictionary<string, object?> { ["code"] = code });
        var id = MessageBuilder.NewMessageId(settings.MailHostname).Trim('<', '>');
        var mail = new MessageBuilder
        {
            From = new MailboxAddress(null, msg.FromAddress),
            To = [new MailboxAddress(null, rcpt.Address)],
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
            MessageId = id,
            TextBody = body,
            ExtraHeaders = [("Auto-Submitted", "auto-generated")],
        }.ToBytes();
        var domain = msg.FromAddress[(msg.FromAddress.LastIndexOf('@') + 1)..];
        await systemMail.SendAsync(msg.TenantId, msg.SenderUserId, msg.FromAddress, rcpt.Address, await systemMail.SignAsync(msg.TenantId, domain, mail, ct),
            notification: false, locale, id, ct);
    }

    public async Task<ConfidentialContentDto> OpenAsync(string token, string? code, CancellationToken ct)
    {
        var (msg, rcpt) = await FindAsync(token, ct);
        if (msg.RequireCode)
        {
            if (rcpt.CodeHash is null || rcpt.CodeExpiresAt is null || rcpt.CodeExpiresAt <= DateTimeOffset.UtcNow || rcpt.CodeAttempts >= MaxCodeAttempts)
            {
                throw AppException.Forbidden("confidential_code_expired");
            }

            var ok = code is { Length: 6 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token + ":" + code)), Encoding.ASCII.GetBytes(rcpt.CodeHash));
            if (!ok)
            {
                rcpt.CodeAttempts++;
                await db.SaveChangesAsync(ct);
                throw AppException.Forbidden("confidential_code_wrong");
            }
        }

        rcpt.Opens++;
        rcpt.FirstOpenedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var raw = new MemoryStream();
        await blobs.ReadAsync(msg.TenantId, msg.BlobSha256, raw, ct: ct);
        raw.Position = 0;
        var parsed = MimeMessage.Parse(raw);
        MimeEntity? html = null, text = null;
        var cids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ConfidentialFileDto>();
        var expires = DateTimeOffset.UtcNow + FileLinkLifetime;
        foreach (var e in parsed.Root.Walk().Where(e => e.IsLeaf))
        {
            var url = FileUrl(token, e.PartPath, expires);
            if (e.ContentId is { } cid)
            {
                cids.TryAdd(cid, url);
            }

            if (e.IsAttachment)
            {
                files.Add(new ConfidentialFileDto(e.FileName ?? "file", e.MediaType, e.BodyLength, url));
            }
            else if (e.MediaType == "text/html")
            {
                html ??= e;
            }
            else if (e.MediaType == "text/plain")
            {
                text ??= e;
            }
        }

        string? document = null;
        if (html is not null)
        {
            // Remote images stay blocked: the reader has no account to allow them with.
            var clean = MailHtml.Sanitize(html.ReadText(raw), cid => cids.GetValueOrDefault(cid), _ => "", showRemote: false);
            document = MailHtml.Document(clean.Html, settings.WebUrl, rtl: false);
        }

        return new ConfidentialContentDto(msg.Subject, msg.FromAddress, parsed.Date ?? msg.CreatedAt, msg.ExpiresAt, document, text?.ReadText(raw), files);
    }

    private string FileUrl(string token, string part, DateTimeOffset expires)
    {
        var exp = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{settings.WebUrl}/api/v1/confidential/{token}/files/{Uri.EscapeDataString(part)}?e={exp}&s={FileSig(token, part, exp)}";
    }

    private string FileSig(string token, string part, string exp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(token + "|" + part + "|" + exp)))[..32];

    /// <summary>A file of the message, through a link handed out by <see cref="OpenAsync"/> (valid 30 minutes).</summary>
    public async Task<(string Name, string ContentType, byte[] Content)> FileAsync(string token, string part, string exp, string sig, CancellationToken ct)
    {
        if (!long.TryParse(exp, NumberStyles.None, CultureInfo.InvariantCulture, out var e) || DateTimeOffset.FromUnixTimeSeconds(e) < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(FileSig(token, part, exp)), Encoding.ASCII.GetBytes(sig)))
        {
            throw AppException.Forbidden("confidential_link_expired");
        }

        var (msg, _) = await FindAsync(token, ct);
        var raw = new MemoryStream();
        await blobs.ReadAsync(msg.TenantId, msg.BlobSha256, raw, ct: ct);
        raw.Position = 0;
        var entity = MimeMessage.Parse(raw).Root.Walk().FirstOrDefault(x => x.IsLeaf && x.PartPath == part) ?? throw AppException.NotFound();
        return (entity.FileName ?? "file", entity.MediaType, entity.ReadDecodedBody(raw));
    }

    // ---------- sender side ----------

    public async Task<ConfidentialStatusDto?> StatusAsync(Guid tenant, Guid user, string rfcMessageId, CancellationToken ct)
    {
        var id = rfcMessageId.Trim('<', '>');
        var msg = await db.ConfidentialMessages.AsNoTracking().FirstOrDefaultAsync(m => m.TenantId == tenant && m.SenderUserId == user && m.RfcMessageId == id && m.DeletedAt == null, ct);
        if (msg is null)
        {
            return null;
        }

        var rcpts = await db.ConfidentialRecipients.AsNoTracking().Where(r => r.ConfidentialMessageId == msg.Id && r.DeletedAt == null).OrderBy(r => r.Address)
            .Select(r => new ConfidentialRecipientDto(r.Address, r.Opens, r.FirstOpenedAt)).ToListAsync(ct);
        return new ConfidentialStatusDto(msg.Id, msg.ExpiresAt, msg.RequireCode, msg.RevokedAt, rcpts);
    }

    public async Task RevokeAsync(Guid tenant, Guid user, Guid id, bool revoke, CancellationToken ct)
    {
        var n = await db.ConfidentialMessages.Where(m => m.Id == id && m.TenantId == tenant && m.SenderUserId == user && m.DeletedAt == null)
            .ExecuteUpdateAsync(m => m.SetProperty(x => x.RevokedAt, revoke ? DateTimeOffset.UtcNow : null).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (n == 0)
        {
            throw AppException.NotFound("confidential_not_found");
        }
    }

    /// <summary>Subject and body in the sender's language followed by English (like DSNs).</summary>
    private static (string Subject, string Body) Texts(string key, string? locale, Dictionary<string, object?> args)
    {
        var cultures = new List<CultureInfo>();
        if (locale is not null)
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(locale);
                if (JsonStringLocalizerFactory.Resolve(c) != "en")
                {
                    cultures.Add(c);
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        cultures.Add(CultureInfo.GetCultureInfo("en"));
        var subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".subject", cultures[0]), cultures[0], args);
        var body = string.Join("\r\n\r\n---\r\n\r\n", cultures.Select(c => JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".body", c), c, args)));
        return (subject, body);
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
