using System.Net;
using System.Text.Json;
using BlueMail.Antispam;
using BlueMail.Application.Features.Antispam;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Mx;

public enum SpamDecision { Inbox, Spam, Quarantine, Reject }

/// <summary>The verdict for one mailbox: where it goes and the score report shown in "Why is this in Spam?".</summary>
public sealed record MailboxVerdict(Guid MailboxId, SpamDecision Decision, double Score, string ReportJson);

/// <summary>
/// Scores an incoming message for each recipient mailbox (PROMPT 2.3): rules + Bayes (global, tenant and the
/// mailbox owner's model) + reputation (the owner wrote to this sender before) + optional DNS block lists.
/// Thresholds come from the tenant's spam settings.
/// </summary>
public sealed class MxSpamFilter(BlueMailDbContext db, BayesStore bayes, DnsblChecker dnsbl)
{
    public static SpamSettings Defaults(Guid tenant) => new() { TenantId = tenant };

    public async Task<SpamSettings> SettingsAsync(Guid tenant, CancellationToken ct) =>
        await db.SpamSettings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant, ct) ?? Defaults(tenant);

    public async Task<IReadOnlyList<MailboxVerdict>> ScoreAsync(Guid tenant, IReadOnlyList<Guid> mailboxes, SpamInput baseInput, IReadOnlyList<object> findings,
        CancellationToken ct)
    {
        var settings = await SettingsAsync(tenant, ct);
        var domains = await db.Domains.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenant && d.DeletedAt == null).Select(d => d.Domain).ToListAsync(ct);
        var fromDomain = SpamRules.DomainOf(baseInput.FromAddress);
        var firstSeen = fromDomain is not null && !await SeenDomainAsync(tenant, fromDomain, ct);
        var hosts = baseInput.Links.Select(l => Uri.TryCreate(l.Href, UriKind.Absolute, out var u) ? u.Host : null).Where(h => h is not null).Select(h => h!);
        var listed = await dnsbl.CheckAsync(tenant, baseInput.RemoteIp, fromDomain, hosts, ct);
        var input = baseInput with { OurDomains = domains, SenderDomainFirstSeen = firstSeen, ListedIn = listed };
        var tokens = SpamTokens.Of(input).ToList();

        var result = new List<MailboxVerdict>();
        foreach (var mb in mailboxes)
        {
            var owner = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.Id == mb).Select(m => new { m.OwnerId, m.OwnerKind }).FirstAsync(ct);
            var models = await bayes.LoadAsync(tenant, owner.OwnerKind == MailboxOwnerKind.User ? owner.OwnerId : null, tokens, ct);
            var known = input.FromAddress is { } from && await WroteToAsync(tenant, mb, from, ct);
            var verdict = SpamRules.Score(input with { SenderKnown = known }, Bayes.Classify(tokens, models));
            var hasSuspiciousFile = input.SuspiciousAttachmentCodes.Count > 0;
            var decision = verdict.Score >= settings.RejectThreshold ? SpamDecision.Reject
                : hasSuspiciousFile && settings.SuspiciousAttachments == SuspiciousAttachmentAction.Reject ? SpamDecision.Reject
                : hasSuspiciousFile && settings.SuspiciousAttachments == SuspiciousAttachmentAction.Quarantine ? SpamDecision.Quarantine
                : verdict.IsSpam(settings.SpamThreshold) ? SpamDecision.Spam
                : SpamDecision.Inbox;
            var report = JsonSerializer.Serialize(new
            {
                spam = new
                {
                    score = verdict.Score,
                    threshold = settings.SpamThreshold,
                    rejectThreshold = settings.RejectThreshold,
                    bayes = verdict.BayesProbability,
                    hits = verdict.Hits.Select(h => new { code = h.Code, score = h.Score, detail = h.Detail }),
                },
                findings,
            });
            result.Add(new MailboxVerdict(mb, decision, verdict.Score, report));
        }

        return result;
    }

    private Task<bool> SeenDomainAsync(Guid tenant, string domain, CancellationToken ct)
    {
        var suffix = "@" + domain;
        return db.Messages.IgnoreQueryFilters().AsNoTracking().AnyAsync(m => m.TenantId == tenant && m.FromAddr != null && m.FromAddr.EndsWith(suffix), ct);
    }

    /// <summary>Reputation: the mailbox sent mail to this address before (PROMPT 2.3 "người dùng đã từng trả lời").</summary>
    private async Task<bool> WroteToAsync(Guid tenant, Guid mailbox, string address, CancellationToken ct)
    {
        var c = (NpgsqlConnection)db.Database.GetDbConnection();
        if (c.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM mail.messages m JOIN mail.message_labels ml ON ml.tenant_id = m.tenant_id AND ml.message_id = m.id
              JOIN mail.labels l ON l.id = ml.label_id
              WHERE m.tenant_id = @t AND m.mailbox_id = @mb AND l.system_key = 'SENT' AND m.expunged_at IS NULL
                AND (m.to_addrs @> @a OR m.cc_addrs @> @a OR m.to_addrs @> @b OR m.cc_addrs @> @b))
            """, c);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("mb", mailbox);
        // Addresses are stored as written; compare the original and the lower-case form.
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new[] { new { address = address.ToLowerInvariant() } }) });
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new[] { new { address } }) });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
