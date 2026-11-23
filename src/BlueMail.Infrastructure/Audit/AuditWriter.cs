using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Domain.Audit;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Infrastructure.Audit;

public sealed record AuditEntry(
    string Action,
    AuditResult Result,
    Guid? TenantId = null,
    Guid? UserId = null,
    string? ActorEmail = null,
    string? Ip = null,
    string? UserAgent = null,
    string? Entity = null,
    string? EntityId = null,
    string? OldValueJson = null,
    string? NewValueJson = null);

/// <summary>
/// Appends to audit.audit_logs keeping a single hash chain. Writers are serialised with a transaction
/// level advisory lock, so two concurrent writers can never link to the same previous row. When the
/// caller already has a transaction open on the context, the audit row commits (or rolls back) with it.
/// </summary>
public sealed class AuditWriter(BlueMailDbContext db)
{
    private const long ChainLockKey = 0x426C75654D61696C; // "BlueMail"

    public async Task WriteAsync(AuditEntry e, CancellationToken ct = default)
    {
        var own = db.Database.CurrentTransaction is null;
        var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [ChainLockKey], ct);
            var prev = await db.AuditLogs.OrderByDescending(x => x.Id).Select(x => x.Hash).FirstOrDefaultAsync(ct) ?? [];
            var row = new AuditLog
            {
                TenantId = e.TenantId,
                UserId = e.UserId,
                ActorEmail = e.ActorEmail,
                Ip = e.Ip,
                UserAgent = e.UserAgent,
                Action = e.Action,
                Entity = e.Entity,
                EntityId = e.EntityId,
                OldValue = e.OldValueJson,
                NewValue = e.NewValueJson,
                Result = e.Result,
                // PostgreSQL keeps microseconds; hash exactly what is stored.
                OccurredAt = TruncateToMicroseconds(DateTimeOffset.UtcNow),
                PrevHash = prev,
            };
            row.Hash = ComputeHash(row);
            db.AuditLogs.Add(row);
            await db.SaveChangesAsync(ct);
            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset t) =>
        new(t.UtcTicks - (t.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>SHA-256 over prev_hash and a canonical, unambiguous encoding of every column.</summary>
    public static byte[] ComputeHash(AuditLog r)
    {
        var sb = new StringBuilder();
        void Field(string? v) => sb.Append(v is null ? "␀" : v.Replace("\u001f", "\u001f\u001f", StringComparison.Ordinal)).Append('\u001f');
        Field(r.TenantId?.ToString("D"));
        Field(r.UserId?.ToString("D"));
        Field(r.ActorEmail);
        Field(r.Ip);
        Field(r.UserAgent);
        Field(r.Action);
        Field(r.Entity);
        Field(r.EntityId);
        Field(CanonicalJson(r.OldValue));
        Field(CanonicalJson(r.NewValue));
        Field(r.Result.ToString());
        Field(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var input = new byte[r.PrevHash.Length + body.Length];
        r.PrevHash.CopyTo(input, 0);
        body.CopyTo(input, r.PrevHash.Length);
        return SHA256.HashData(input);
    }

    /// <summary>
    /// jsonb does not keep the original text (key order, whitespace), so the hash covers a canonical form:
    /// object keys sorted ordinally, no whitespace, strings re-escaped, numbers as written.
    /// </summary>
    public static string? CanonicalJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            Write(doc.RootElement, w);
        }

        return Encoding.UTF8.GetString(ms.ToArray());

        static void Write(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        w.WritePropertyName(p.Name);
                        Write(p.Value, w);
                    }

                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray())
                    {
                        Write(item, w);
                    }

                    w.WriteEndArray();
                    break;
                default:
                    e.WriteTo(w);
                    break;
            }
        }
    }
}

public sealed record AuditVerification(bool Valid, long Checked, long? FirstBrokenId);

public sealed class AuditChainVerifier(BlueMailDbContext db)
{
    /// <summary>Walks the whole chain in id order and reports the first row whose links or content do not match.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken ct = default)
    {
        byte[] prev = [];
        long count = 0, lastId = 0;
        while (true)
        {
            var batch = await db.AuditLogs.AsNoTracking().Where(x => x.Id > lastId).OrderBy(x => x.Id).Take(1000).ToListAsync(ct);
            if (batch.Count == 0)
            {
                return new AuditVerification(true, count, null);
            }

            foreach (var row in batch)
            {
                if (!CryptographicOperations.FixedTimeEquals(row.PrevHash, prev) ||
                    !CryptographicOperations.FixedTimeEquals(AuditWriter.ComputeHash(row), row.Hash))
                {
                    return new AuditVerification(false, count, row.Id);
                }

                prev = row.Hash;
                lastId = row.Id;
                count++;
            }
        }
    }
}
