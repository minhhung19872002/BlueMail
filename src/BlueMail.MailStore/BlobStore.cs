using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BlueMail.Infrastructure.Security;

namespace BlueMail.MailStore;

/// <summary>
/// Raw message content on S3 (bucket bm-messages), encrypted per tenant, keyed by SHA-256 of the plaintext within the
/// tenant: a message sent to 50 people of one tenant is stored once (PROMPT 2.5).
/// </summary>
public sealed class BlobStore(IAmazonS3 s3, TenantKeyProvider keys)
{
    public const string Bucket = "bm-messages";

    public static string ObjectKey(Guid tenantId, byte[] sha256) => $"t/{tenantId:N}/{Convert.ToHexStringLower(sha256)}";

    public static async Task<byte[]> HashAsync(Stream content, CancellationToken ct)
    {
        content.Position = 0;
        var hash = await SHA256.HashDataAsync(content, ct);
        content.Position = 0;
        return hash;
    }

    /// <summary>Encrypts and uploads <paramref name="content"/> (seekable). Returns when S3 has acknowledged the write.</summary>
    public async Task PutAsync(Guid tenantId, byte[] sha256, Stream content, CancellationToken ct)
    {
        var key = await keys.GetAsync(tenantId, ct);
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "bm-blob-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (temp)
        {
            content.Position = 0;
            await ChunkedAead.EncryptAsync(content, content.Length, temp, key, tenantId, sha256, ct);
            temp.Position = 0;
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = ObjectKey(tenantId, sha256),
                InputStream = temp,
                AutoCloseStream = false,
                ContentType = "application/octet-stream",
                UseChunkEncoding = false, // Garage and other S3 servers vary in aws-chunked support
            }, ct);
        }
    }

    public async Task<bool> ExistsAsync(Guid tenantId, byte[] sha256, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(Bucket, ObjectKey(tenantId, sha256), ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Decrypts plaintext bytes [offset, offset+length) into <paramref name="output"/>; whole object when length is null.
    /// A response from S3 that breaks off (the store closed a kept-alive connection under load) is read again, from the
    /// first byte not yet written, up to <see cref="ReadAttempts"/> times: a FETCH of thousands of messages over IMAP
    /// must not fail on one dropped storage connection (docs/08 BUG-078).
    /// </summary>
    public async Task ReadAsync(Guid tenantId, byte[] sha256, Stream output, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        var written = new CountingStream(output);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ReadOnceAsync(tenantId, sha256, written, offset + written.Count, length is { } l ? l - written.Count : null, ct);
                return;
            }
            catch (Exception e) when (attempt < ReadAttempts && !ct.IsCancellationRequested && !written.Failed
                                      && e is HttpIOException or HttpRequestException or EndOfStreamException)
            {
                await Task.Delay(20 * attempt, ct); // short: a lost block fails all attempts, and a FETCH may meet hundreds
            }
        }
    }

    public const int ReadAttempts = 3;

    private async Task ReadOnceAsync(Guid tenantId, byte[] sha256, Stream output, long offset, long? length, CancellationToken ct)
    {
        if (length <= 0)
        {
            return;
        }

        var key = await keys.GetAsync(tenantId, ct);
        var objectKey = ObjectKey(tenantId, sha256);
        byte[] headerBytes;
        using (var head = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = objectKey, ByteRange = new ByteRange(0, ChunkedAead.HeaderSize - 1) }, ct))
        {
            headerBytes = new byte[ChunkedAead.HeaderSize];
            await head.ResponseStream.ReadExactlyAsync(headerBytes, ct);
        }

        var h = ChunkedAead.ReadHeader(headerBytes);
        var take = Math.Min(length ?? h.PlaintextLength, Math.Max(0, h.PlaintextLength - offset));
        if (take <= 0)
        {
            return;
        }

        var first = offset / h.ChunkSize;
        var last = (offset + take - 1) / h.ChunkSize;
        var (start, end) = h.CipherRange(first, last);
        using var body = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = objectKey, ByteRange = new ByteRange(start, end) }, ct);
        await ChunkedAead.DecryptAsync(body.ResponseStream, h, first, last, output, key, tenantId, sha256, offset - (first * h.ChunkSize), take, ct);
    }

    /// <summary>Counts what reached the caller's stream; a failure of that stream itself (the reader went away) is not retried.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }

        public bool Failed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Count; set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(buffer, cancellationToken);
            }
            catch
            {
                Failed = true;
                throw;
            }

            Count += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                inner.Write(buffer, offset, count);
            }
            catch
            {
                Failed = true;
                throw;
            }

            Count += count;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public Task DeleteAsync(Guid tenantId, byte[] sha256, CancellationToken ct) =>
        s3.DeleteObjectAsync(Bucket, ObjectKey(tenantId, sha256), ct);
}
