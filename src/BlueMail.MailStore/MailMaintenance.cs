using Npgsql;

namespace BlueMail.MailStore;

public sealed partial class PgMailStore
{
    /// <summary>
    /// Deletes forever what has been in Trash or Spam for longer than <paramref name="age"/> (PROMPT 6.5: 30 days),
    /// except messages under legal hold (the expunge itself skips them). Returns how many were expunged.
    /// Runs as the system, not as a user: it goes through the normal change path (modseq, label log, events).
    /// </summary>
    public async Task<int> PurgeTrashAndSpamAsync(TimeSpan age, int limit, CancellationToken ct = default)
    {
        var c = await OpenConnAsync(ct);
        var candidates = new List<(Guid Tenant, Guid Mailbox, Guid Id)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT m.tenant_id, m.mailbox_id, m.id
            FROM mail.message_labels ml
            JOIN mail.labels l ON l.id = ml.label_id AND l.system_key IN ('TRASH', 'SPAM')
            JOIN mail.messages m ON m.tenant_id = ml.tenant_id AND m.id = ml.message_id
            WHERE m.expunged_at IS NULL AND m.deleted_at IS NULL AND m.legal_hold_count = 0
              AND (SELECT max(lc.occurred_at) FROM mail.label_changes lc
                   WHERE lc.tenant_id = ml.tenant_id AND lc.label_id = ml.label_id AND lc.imap_uid = ml.imap_uid AND lc.change = 'ADDED') < now() - @age
            LIMIT @lim
            """, c))
        {
            cmd.Parameters.AddWithValue("age", age);
            cmd.Parameters.AddWithValue("lim", limit);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                candidates.Add((rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2)));
            }
        }

        var done = 0;
        foreach (var g in candidates.GroupBy(x => (x.Tenant, x.Mailbox)))
        {
            var system = new MailboxAccess(g.Key.Tenant, g.Key.Mailbox, Guid.Empty, MailboxRights.Owner);
            done += (await ApplyAsync(system, g.Select(x => x.Id).ToList(), new MessageChange { Expunge = true }, ct)).Messages;
        }

        return done;
    }

    /// <summary>Access of the system to a mailbox, for background work on behalf of its owner (snooze wake-up).</summary>
    public static MailboxAccess SystemAccess(Guid tenantId, Guid mailboxId) => new(tenantId, mailboxId, Guid.Empty, MailboxRights.Owner);
}
