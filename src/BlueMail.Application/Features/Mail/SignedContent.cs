using System.IO.Compression;
using BlueMail.Application.Common;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Net;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;

namespace BlueMail.Application.Features.Mail;

public sealed record ServedContent(byte[] Content, string ContentType, string? FileName, bool Download);

/// <summary>
/// Serves what a signed URL grants. The signature proves BlueMail issued it; access to the mailbox is checked
/// again (a revoked shared-mailbox grant stops working before the URL expires).
/// </summary>
public sealed class SignedContent(SignedUrls signer, IMailStore stores, PgMailStore store, SafeHttpFetcher fetcher)
{
    public async Task<ServedContent> ServeAsync(string token, SignedKind expected, CancellationToken ct)
    {
        var g = signer.Verify(token);
        if (g is null || (g.Kind == SignedKind.Image) != (expected == SignedKind.Image))
        {
            throw AppException.NotFound("link_expired");
        }

        MailboxAccess access;
        try
        {
            access = await stores.OpenAsync(g.TenantId, g.UserId, g.MailboxId, ct);
        }
        catch (MailboxAccessDeniedException)
        {
            throw AppException.NotFound("link_expired");
        }

        switch (g.Kind)
        {
            case SignedKind.Image:
                try
                {
                    var img = await fetcher.FetchAsync(g.Url ?? "", ct);
                    return new ServedContent(img.Content, img.ContentType, null, false);
                }
                catch (FetchException e)
                {
                    throw e.Reason == FetchFailure.Forbidden ? AppException.Forbidden("image_blocked") : AppException.NotFound("image_unavailable");
                }

            case SignedKind.Raw:
            {
                using var ms = new MemoryStream();
                if (!await store.CopyRawAsync(access, g.MessageId, ms, ct))
                {
                    throw AppException.NotFound("message_not_found");
                }

                var m = await store.GetMessageAsync(access, g.MessageId, ct);
                return new ServedContent(ms.ToArray(), "message/rfc822", FileNameFor(m?.Subject, ".eml"), true);
            }

            case SignedKind.Part:
            {
                var part = await store.GetPartAsync(access, g.MessageId, g.Part ?? "", ct) ?? throw AppException.NotFound("attachment_not_found");
                if (part.Info.VirusStatus == VirusStatus.Infected)
                {
                    throw AppException.Forbidden("attachment_infected");
                }

                var type = GetThreadHandler.Viewable.Contains(part.Info.ContentType) ? part.Info.ContentType.ToLowerInvariant() : "application/octet-stream";
                var download = g.Download || type == "application/octet-stream";
                return new ServedContent(part.Content, type == "text/plain" ? "text/plain; charset=utf-8" : type,
                    SafeFileName(part.Info.FileName ?? "attachment"), download);
            }

            case SignedKind.Zip:
            {
                var m = await store.GetMessageAsync(access, g.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
                using var zipStream = new MemoryStream();
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in m.Attachments.Where(a => !a.Inline && a.VirusStatus != VirusStatus.Infected))
                    {
                        var part = await store.GetPartAsync(access, m.Id, a.PartPath, ct);
                        if (part is null)
                        {
                            continue;
                        }

                        var name = Unique(SafeFileName(a.FileName ?? "attachment"), used);
                        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        await using var es = await entry.OpenAsync(ct);
                        await es.WriteAsync(part.Value.Content, ct);
                    }
                }

                return new ServedContent(zipStream.ToArray(), "application/zip", FileNameFor(m.Subject, ".zip"), true);
            }

            default:
                throw AppException.NotFound("link_expired");
        }
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{stem} ({i}){ext}";
        }

        return candidate;
    }

    /// <summary>A file name safe on every OS: no path parts, control or reserved characters, never empty or dots only.</summary>
    public static string SafeFileName(string name)
    {
        var lastSep = name.LastIndexOfAny(['/', '\\']);
        if (lastSep >= 0)
        {
            name = name[(lastSep + 1)..]; // keep the file name, drop any path the sender put in front of it
        }

        var chars = name.Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        s = s.TrimStart('.');
        if (s.Length > 200)
        {
            var ext = Path.GetExtension(s);
            s = s[..(200 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : "");
        }

        return s.Length == 0 ? "attachment" : s;
    }

    private static string FileNameFor(string? subject, string ext)
    {
        var s = SafeFileName(string.IsNullOrWhiteSpace(subject) ? "message" : subject);
        return (s.Length > 80 ? s[..80] : s) + ext;
    }
}
