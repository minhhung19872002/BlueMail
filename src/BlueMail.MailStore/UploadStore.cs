using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BlueMail.Infrastructure.Security;

namespace BlueMail.MailStore;

/// <summary>
/// Storage of uploaded files (compose attachments, large-file links, mbox imports) on S3 bucket bm-attachments-large.
/// </summary>
public sealed class UploadStore(IAmazonS3 s3, TenantKeyProvider keys) : ChunkedObjectStore(s3, keys, Bucket, "u")
{
    public const string Bucket = "bm-attachments-large";

    public static string ChunkKey(Guid tenantId, Guid uploadId, int index) => $"u/{tenantId:N}/{uploadId:N}/{index:D6}";
}

/// <summary>Finished exports (user data, eDiscovery, tenant) on S3 bucket bm-exports, same encryption as uploads.</summary>
public sealed class ExportStore(IAmazonS3 s3, TenantKeyProvider keys) : ChunkedObjectStore(s3, keys, "bm-exports", "x");

/// <summary>
/// A file stored as chunks, one S3 object each, encrypted with the tenant key (<see cref="ChunkedAead"/>, bound to the
/// file id and chunk index so chunks cannot be swapped). Reading concatenates the chunks in order.
/// </summary>
public abstract class ChunkedObjectStore(IAmazonS3 s3, TenantKeyProvider keys, string bucket, string prefix)
{
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    private string Key(Guid tenantId, Guid fileId, int index) => $"{prefix}/{tenantId:N}/{fileId:N}/{index:D6}";

    private static byte[] Binding(Guid uploadId, int index)
    {
        var b = new byte[20];
        uploadId.TryWriteBytes(b);
        BitConverter.TryWriteBytes(b.AsSpan(16), index);
        return SHA256.HashData(b);
    }

    /// <summary>Stores a whole local file in chunks; returns the number of chunks.</summary>
    public async Task<int> PutFileAsync(Guid tenantId, Guid fileId, string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var buffer = new byte[MaxChunkBytes];
        var index = 0;
        while (true)
        {
            var n = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            if (n == 0 && index > 0)
            {
                return index;
            }

            await PutChunkAsync(tenantId, fileId, index++, buffer[..n], ct);
            if (n < buffer.Length)
            {
                return index;
            }
        }
    }

    public async Task PutChunkAsync(Guid tenantId, Guid uploadId, int index, byte[] data, CancellationToken ct)
    {
        var key = await keys.GetAsync(tenantId, ct);
        using var cipher = new MemoryStream();
        await ChunkedAead.EncryptAsync(new MemoryStream(data), data.Length, cipher, key, tenantId, Binding(uploadId, index), ct);
        cipher.Position = 0;
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = Key(tenantId, uploadId, index), InputStream = cipher, AutoCloseStream = false, UseChunkEncoding = false,
        }, ct);
    }

    /// <summary>Writes the whole file (decrypted) to <paramref name="output"/>.</summary>
    public async Task CopyToAsync(Guid tenantId, Guid uploadId, int chunks, Stream output, CancellationToken ct)
    {
        var key = await keys.GetAsync(tenantId, ct);
        for (var i = 0; i < chunks; i++)
        {
            using var obj = await s3.GetObjectAsync(bucket, Key(tenantId, uploadId, i), ct);
            using var ms = new MemoryStream();
            await obj.ResponseStream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var h = ChunkedAead.ReadHeader(bytes);
            var body = new MemoryStream(bytes, ChunkedAead.HeaderSize, bytes.Length - ChunkedAead.HeaderSize);
            await ChunkedAead.DecryptAsync(body, h, 0, h.Chunks - 1, output, key, tenantId, Binding(uploadId, i), 0, h.PlaintextLength, ct);
        }
    }

    public async Task<byte[]> ReadAllAsync(Guid tenantId, Guid uploadId, int chunks, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await CopyToAsync(tenantId, uploadId, chunks, ms, ct);
        return ms.ToArray();
    }

    public async Task DeleteAsync(Guid tenantId, Guid uploadId, int chunks, CancellationToken ct)
    {
        for (var i = 0; i < chunks; i++)
        {
            await s3.DeleteObjectAsync(bucket, Key(tenantId, uploadId, i), ct);
        }
    }
}
