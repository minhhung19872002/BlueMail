using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.MailStore;

/// <summary>A message found by a compliance search: which mailbox, when received, whether the user had deleted it.</summary>
public sealed record ComplianceHit(Guid MessageId, Guid MailboxId, DateTimeOffset ReceivedAt, bool HiddenFromUser);

public sealed partial class PgMailStore
{
    /// <summary>
    /// Search over many mailboxes of a tenant for compliance work (PROMPT 7.5: eDiscovery, query holds, retention):
    /// the same query language as the user's search, Spam and Trash included, and - for eDiscovery and holds - also
    /// messages the user deleted that a hold or KEEP_MIN retention still keeps. Keyset paging, newest first.
    /// Runs as the system: the caller has checked the compliance permission.
    /// </summary>
    public async Task<IReadOnlyList<ComplianceHit>> ComplianceSearchAsync(Guid tenantId, IReadOnlyCollection<Guid>? mailboxIds, Search.SearchNode? node,
        DateTimeOffset? receivedBefore, bool includeHidden, (DateTimeOffset At, Guid Id)? after, int limit, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        var (where, ps) = node is null ? ("TRUE", (IReadOnlyList<NpgsqlParameter>)[]) : Search.SearchSql.Translate(node, []);
        await using var cmd = new NpgsqlCommand($"""
            SELECT m.id, m.mailbox_id, m.received_at, m.deleted_at IS NOT NULL FROM mail.messages m
            WHERE m.tenant_id = @t AND (@all OR m.mailbox_id = ANY(@mbs)) AND m.expunged_at IS NULL AND (@withHidden OR m.deleted_at IS NULL)
              AND (@before IS NULL OR m.received_at < @before) AND {where}
              AND (@aAt IS NULL OR (m.received_at, m.id) < (@aAt, @aId))
            ORDER BY m.received_at DESC, m.id DESC LIMIT @lim
            """, c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("all", mailboxIds is null);
        cmd.Parameters.AddWithValue("mbs", (mailboxIds ?? []).ToArray());
        cmd.Parameters.AddWithValue("withHidden", includeHidden);
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = receivedBefore is { } rb ? rb : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("aAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.At : DBNull.Value });
        cmd.Parameters.AddWithValue("aId", after?.Id ?? Guid.Empty);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 5000));
        foreach (var p in ps)
        {
            cmd.Parameters.Add(p.Clone());
        }

        var list = new List<ComplianceHit>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new ComplianceHit(rd.GetGuid(0), rd.GetGuid(1), rd.GetFieldValue<DateTimeOffset>(2), rd.GetBoolean(3)));
        }

        return list;
    }

    /// <summary>The raw message for an export, hidden (deleted but kept) messages included.</summary>
    public async Task<bool> CopyRawForComplianceAsync(Guid tenantId, Guid messageId, Stream output, CancellationToken ct = default)
    {
        var row = await (from m in db.Messages.IgnoreQueryFilters()
                         where m.TenantId == tenantId && m.Id == messageId && m.ExpungedAt == null
                         select new { m.BlobSha256 }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return false;
        }

        await blobs.ReadAsync(tenantId, row.BlobSha256, output, ct: ct);
        return true;
    }
}
