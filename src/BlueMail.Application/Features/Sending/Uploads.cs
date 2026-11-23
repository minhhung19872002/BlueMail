using BlueMail.Application.Common;
using BlueMail.Application.Features.Mail;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Scanner;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Sending;

public sealed record UploadInfo(Guid Id, string FileName, string ContentType, long Length, long Offset, string? ScanStatus);

/// <summary>
/// Resumable uploads (tus 1.0 core: creation, HEAD offset, PATCH append; PROMPT 6.4). A PATCH must start at the
/// stored offset, so a resumed upload never duplicates or skips bytes. When the last byte arrives the file is
/// scanned (BlueMail.Scanner, tenant policy for blocked extensions).
/// </summary>
public sealed class UploadService(BlueMailDbContext db, UploadStore store, IFileScanner scanner, ICurrentUser me, Admin.Policies.PolicyService policies)
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxOpenUploads = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);

    public async Task<UploadInfo> CreateAsync(string fileName, string? contentType, long length, CancellationToken ct)
    {
        if (length is < 0 or > MaxFileBytes)
        {
            throw AppException.BadRequest("upload_too_large");
        }

        var open = await db.Uploads.CountAsync(u => u.UserId == me.UserId && u.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (open >= MaxOpenUploads)
        {
            throw AppException.BadRequest("too_many_uploads");
        }

        var name = SignedContent.SafeFileName(string.IsNullOrWhiteSpace(fileName) ? "file" : fileName);
        var blocked = await policies.ListAsync(me.TenantId, await policies.OrgUnitOfAsync(me.UserId, ct), Admin.Policies.PolicyKeys.BlockedExtensions, ct);
        if (blocked.Any(b => name.EndsWith("." + b.TrimStart('.'), StringComparison.OrdinalIgnoreCase)))
        {
            throw AppException.BadRequest("attachment_type_blocked"); // policy blocked_extensions (PROMPT 7.4)
        }

        var u = new Upload
        {
            TenantId = me.TenantId,
            UserId = me.UserId,
            FileName = name,
            ContentType = string.IsNullOrWhiteSpace(contentType) || contentType.Length > 255 ? "application/octet-stream" : contentType,
            Length = length,
            ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
        };
        db.Uploads.Add(u);
        await db.SaveChangesAsync(ct);
        if (length == 0)
        {
            await FinishAsync(u, ct);
        }

        return Info(u);
    }

    public async Task<UploadInfo> GetAsync(Guid id, CancellationToken ct) => Info(await Find(id, ct));

    public async Task<UploadInfo> AppendAsync(Guid id, long offset, Stream body, long? contentLength, CancellationToken ct)
    {
        var u = await Find(id, ct);
        if (offset != u.Offset)
        {
            throw AppException.Conflict("upload_offset_mismatch", new Dictionary<string, object?> { ["offset"] = u.Offset });
        }

        var remaining = u.Length - u.Offset;
        var max = (int)Math.Min(UploadStore.MaxChunkBytes, remaining);
        if (contentLength is { } cl && (cl > max || cl <= 0))
        {
            throw AppException.BadRequest("upload_chunk_size");
        }

        var buffer = new byte[max];
        var read = 0;
        int n;
        while (read < max && (n = await body.ReadAsync(buffer.AsMemory(read, max - read), ct)) > 0)
        {
            read += n;
        }

        if (read == 0 || (read == max && await body.ReadAsync(new byte[1], ct) > 0))
        {
            throw AppException.BadRequest("upload_chunk_size");
        }

        var chunk = read == max ? buffer : buffer[..read];
        await store.PutChunkAsync(u.TenantId, u.Id, u.Chunks, chunk, ct);
        // Only this conditional update moves the offset: two racing PATCHes cannot both be accepted.
        var updated = await db.Uploads.Where(x => x.Id == u.Id && x.Offset == offset && x.Chunks == u.Chunks)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Offset, offset + read).SetProperty(y => y.Chunks, u.Chunks + 1).SetProperty(y => y.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (updated == 0)
        {
            throw AppException.Conflict("upload_offset_mismatch");
        }

        u.Offset = offset + read;
        u.Chunks++;
        if (u.Offset == u.Length)
        {
            await FinishAsync(u, ct);
        }

        return Info(u);
    }

    private async Task FinishAsync(Upload u, CancellationToken ct)
    {
        using var content = new MemoryStream();
        await store.CopyToAsync(u.TenantId, u.Id, u.Chunks, content, ct);
        content.Position = 0;
        var result = await scanner.ScanAsync(content, u.FileName, ct);
        var status = result.Status switch
        {
            ScanStatus.Malicious => nameof(VirusStatus.Infected),
            ScanStatus.Suspicious => nameof(VirusStatus.Suspicious),
            _ => nameof(VirusStatus.Clean),
        };
        await db.Uploads.Where(x => x.Id == u.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ScanStatus, status), ct);
        u.ScanStatus = status;
    }

    public async Task<(UploadInfo Info, byte[] Content)> ReadAsync(Guid id, CancellationToken ct)
    {
        var u = await Find(id, ct);
        if (u.Offset != u.Length)
        {
            throw AppException.BadRequest("upload_incomplete");
        }

        return (Info(u), await store.ReadAllAsync(u.TenantId, u.Id, u.Chunks, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var u = await Find(id, ct);
        u.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await store.DeleteAsync(u.TenantId, u.Id, u.Chunks, ct);
    }

    private async Task<Upload> Find(Guid id, CancellationToken ct) =>
        await db.Uploads.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId && x.ExpiresAt > DateTimeOffset.UtcNow, ct)
        ?? throw AppException.NotFound("upload_not_found");

    private static UploadInfo Info(Upload u) => new(u.Id, u.FileName, u.ContentType, u.Length, u.Offset, u.ScanStatus);
}

/// <summary>Download links for files above the attachment limit (PROMPT 6.4): 30 days, counted, token stored hashed.</summary>
public sealed class LargeFiles(BlueMailDbContext db, UploadStore store)
{
    public sealed record LargeFile(string FileName, string ContentType, long Size, Func<Stream, CancellationToken, Task> WriteTo);

    public async Task<LargeFile?> OpenAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 16 or > 64)
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));
        var f = await db.LargeAttachments.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash && x.DeletedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (f is null)
        {
            return null;
        }

        var uploadId = Guid.ParseExact(f.ObjectKey.Split('/')[2], "N");
        var up = await db.Uploads.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == uploadId && u.TenantId == f.TenantId, ct);
        if (up is null)
        {
            return null;
        }

        await db.LargeAttachments.IgnoreQueryFilters().Where(x => x.Id == f.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.DownloadCount, y => y.DownloadCount + 1), ct);
        return new LargeFile(f.FileName, f.ContentType, f.Size, (output, token2) => store.CopyToAsync(f.TenantId, up.Id, up.Chunks, output, token2));
    }
}
