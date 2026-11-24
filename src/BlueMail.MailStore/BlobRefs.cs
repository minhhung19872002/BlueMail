using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>
/// Content-addressed blobs on S3 (one object per tenant and SHA-256) and their clean-up.
/// <para>
/// Every writer goes through <see cref="EnsureAsync"/>: it marks the blob as used now (<c>mail.blobs.last_used_at</c>),
/// uploads it when there is no row or the object is gone, and only then may the caller store its reference. The
/// collector (<see cref="CollectAsync"/>) deletes the S3 object only when nothing refers to it and it has not been used
/// for <see cref="Grace"/>, and marks the row <c>collected_at</c> (the row stays: expunged messages keep pointing to it).
/// It deletes the object while it holds the row lock, so a writer that wants the same blob waits, then finds the object
/// gone and uploads it again. A writer that marked the row before is protected by the grace time until its reference
/// row commits.
/// </para>
/// </summary>
public static class BlobRefs
{
    /// <summary>Longer than any writer needs between <see cref="EnsureAsync"/> and committing its reference.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(2);

    public static async Task EnsureAsync(BlueMailDbContext db, BlobStore blobs, Guid tenantId, byte[] sha256, Stream content, CancellationToken ct)
    {
        var marked = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE mail.blobs SET last_used_at = now(), collected_at = NULL WHERE tenant_id = {tenantId} AND sha256 = {sha256}", ct);
        // Checked after the UPDATE (which waits for a running collection): a collected or missing object is uploaded again.
        if (marked == 0 || !await blobs.ExistsAsync(tenantId, sha256, ct))
        {
            content.Position = 0;
            await blobs.PutAsync(tenantId, sha256, content, ct);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mail.blobs (tenant_id, sha256, object_key, size, created_at, last_used_at)
            VALUES ({tenantId}, {sha256}, {BlobStore.ObjectKey(tenantId, sha256)}, {content.Length}, now(), now())
            ON CONFLICT (tenant_id, sha256) DO UPDATE SET last_used_at = now(), collected_at = NULL
            """, ct);
    }

    /// <summary>What still needs a blob: live messages, the outbound queue, quarantine, confidential mail, contact photos.</summary>
    internal const string Unreferenced = """
        NOT EXISTS (SELECT 1 FROM mail.messages m WHERE m.tenant_id = b.tenant_id AND m.blob_sha256 = b.sha256 AND m.expunged_at IS NULL)
        AND NOT EXISTS (SELECT 1 FROM mta.outbound_messages o WHERE o.tenant_id = b.tenant_id AND o.blob_sha256 = b.sha256)
        AND NOT EXISTS (SELECT 1 FROM admin.quarantine q WHERE q.tenant_id = b.tenant_id AND q.blob_sha256 = b.sha256)
        AND NOT EXISTS (SELECT 1 FROM mail.confidential_messages c WHERE c.tenant_id = b.tenant_id AND c.blob_sha256 = b.sha256)
        AND NOT EXISTS (SELECT 1 FROM contact.contacts p WHERE p.tenant_id = b.tenant_id AND p.photo_sha256 = b.sha256)
        """;

    /// <summary>Deletes the objects of up to <paramref name="limit"/> unreferenced blobs unused for <paramref name="grace"/>; returns how many.</summary>
    public static async Task<int> CollectAsync(BlueMailDbContext db, BlobStore blobs, TimeSpan grace, int limit, ILogger log, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE mail.blobs d SET collected_at = now() FROM (
                SELECT b.tenant_id, b.sha256 FROM mail.blobs b
                WHERE b.collected_at IS NULL AND b.last_used_at < now() - @grace AND {Unreferenced}
                ORDER BY b.last_used_at LIMIT @limit FOR UPDATE SKIP LOCKED) x
            WHERE d.tenant_id = x.tenant_id AND d.sha256 = x.sha256
            RETURNING d.tenant_id, d.sha256
            """, conn, (NpgsqlTransaction)tx.GetDbTransaction());
        cmd.Parameters.AddWithValue("grace", grace);
        cmd.Parameters.AddWithValue("limit", limit);
        var gone = new List<(Guid Tenant, byte[] Sha)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                gone.Add((r.GetGuid(0), (byte[])r[1]));
            }
        }

        // Objects go while the rows are still locked (see the class remarks); any failure rolls the rows back.
        foreach (var (tenant, sha) in gone)
        {
            await blobs.DeleteAsync(tenant, sha, ct);
        }

        await tx.CommitAsync(ct);
        if (gone.Count > 0)
        {
            log.LogInformation("Deleted {Count} unreferenced blobs", gone.Count);
        }

        return gone.Count;
    }
}
