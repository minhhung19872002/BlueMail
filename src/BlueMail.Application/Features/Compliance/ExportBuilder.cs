using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Features.Contacts;
using BlueMail.Domain.Compliance;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Compliance;

/// <summary>
/// Writes export ZIP files (PROMPT 7.5, 7.7) to a local file: mbox per label (mboxrd, readable by Python's mailbox and
/// Thunderbird), contacts as vCard, settings as JSON, and for eDiscovery a manifest with the SHA-256 of every message.
/// </summary>
public sealed class ExportBuilder(BlueMailDbContext db, PgMailStore store, BlobStore blobs)
{
    /// <summary>Upper bound of messages in one eDiscovery export; the request says so when it is reached.</summary>
    public const int MaxEDiscoveryMessages = 200_000;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Progress(Func<int, Task> Report);

    public async Task UserDataAsync(ZipArchive zip, Guid tenantId, Guid userId, string prefix, Func<Task> tick, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.OwnerId == userId && m.DeletedAt == null).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (mailbox is { } mb)
        {
            await MailboxAsync(zip, tenantId, mb, prefix + "mail/", tick, ct);
        }

        // Contacts (vCard 4.0) and settings: what the user set up, in formats other programs read.
        var contacts = await db.Contacts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.UserId == userId && c.DeletedAt == null && !c.IsAutoSaved).OrderBy(c => c.DisplayName).ToListAsync(ct);
        var cards = new List<ContactCard>();
        foreach (var c in contacts)
        {
            var card = ContactMapping.ToCard(c);
            if (c.PhotoSha256 is not null)
            {
                using var ms = new MemoryStream();
                await blobs.ReadAsync(tenantId, c.PhotoSha256, ms, ct: ct);
                card = card with { Photo = ms.ToArray(), PhotoType = c.PhotoContentType };
            }

            cards.Add(card);
        }

        await TextAsync(zip, prefix + "contacts.vcf", VCard.Write(cards), ct);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var settings = await db.UserSettings.IgnoreQueryFilters().AsNoTracking().Where(s => s.UserId == userId).Select(s => s.Settings).FirstOrDefaultAsync(ct);
        var filters = await db.Filters.IgnoreQueryFilters().AsNoTracking().Where(f => f.UserId == userId && f.DeletedAt == null)
            .Select(f => new { f.Criteria, f.Actions, f.IsEnabled }).ToListAsync(ct);
        var signatures = await db.Signatures.IgnoreQueryFilters().AsNoTracking().Where(s => s.UserId == userId && s.DeletedAt == null)
            .Select(s => new { s.Name, s.Html }).ToListAsync(ct);
        await TextAsync(zip, prefix + "settings.json", JsonSerializer.Serialize(new
        {
            profile = new { email = user.PrimaryEmail, name = user.DisplayName, user.Locale, user.TimeZone },
            settings = settings is null ? (JsonElement?)null : JsonDocument.Parse(settings).RootElement,
            filters = filters.Select(f => new { criteria = f.Criteria, actions = JsonDocument.Parse(f.Actions).RootElement, enabled = f.IsEnabled }),
            signatures,
        }, Pretty), ct);
    }

    /// <summary>One mbox per label of the mailbox; mail without labels (archived) goes to "Archive.mbox".</summary>
    public async Task MailboxAsync(ZipArchive zip, Guid tenantId, Guid mailboxId, string prefix, Func<Task> tick, CancellationToken ct)
    {
        var access = PgMailStore.SystemAccess(tenantId, mailboxId);
        var labels = await store.GetLabelsAsync(access, ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels.Where(l => l.SystemKey is not (SystemLabels.Snoozed or SystemLabels.Scheduled) && !(l.SystemKey ?? "").StartsWith("CATEGORY_", StringComparison.Ordinal)))
        {
            var ids = await (from ml in db.MessageLabels.IgnoreQueryFilters()
                             join m in db.Messages.IgnoreQueryFilters() on ml.MessageId equals m.Id
                             where ml.TenantId == tenantId && ml.LabelId == label.Id && m.ExpungedAt == null && m.DeletedAt == null
                             orderby m.ReceivedAt
                             select new Row(m.Id, m.BlobSha256, m.FromAddr, m.ReceivedAt)).ToListAsync(ct);
            if (ids.Count == 0)
            {
                continue;
            }

            await MboxEntryAsync(zip, prefix + Unique(names, FileName(LabelPath(label, labels))) + ".mbox", tenantId, ids, tick, ct);
        }

        var archived = await (from m in db.Messages.IgnoreQueryFilters()
                              where m.TenantId == tenantId && m.MailboxId == mailboxId && m.ExpungedAt == null && m.DeletedAt == null
                                    && !db.MessageLabels.IgnoreQueryFilters().Any(ml => ml.TenantId == tenantId && ml.MessageId == m.Id)
                              orderby m.ReceivedAt
                              select new Row(m.Id, m.BlobSha256, m.FromAddr, m.ReceivedAt)).ToListAsync(ct);
        if (archived.Count > 0)
        {
            await MboxEntryAsync(zip, prefix + Unique(names, "Archive") + ".mbox", tenantId, archived, tick, ct);
        }
    }

    private sealed record Row(Guid Id, byte[] Sha, string? From, DateTimeOffset ReceivedAt);

    private async Task MboxEntryAsync(ZipArchive zip, string name, Guid tenantId, IReadOnlyList<Row> rows, Func<Task> tick, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var output = entry.Open();
        foreach (var r in rows)
        {
            using var raw = new MemoryStream();
            await blobs.ReadAsync(tenantId, r.Sha, raw, ct: ct);
            raw.Position = 0;
            await Mbox.WriteMessageAsync(output, r.From, r.ReceivedAt, raw, ct);
            await tick();
        }
    }

    /// <summary>
    /// eDiscovery: the messages found, per mailbox as mbox or one .eml each, and manifest.csv listing mailbox, ids, date,
    /// subject and the SHA-256 of the exported bytes. Returns how many messages were written.
    /// </summary>
    public async Task<int> EDiscoveryAsync(ZipArchive zip, DataExport export, IReadOnlyList<ComplianceHit> hits, Func<Task> tick, CancellationToken ct)
    {
        var mailboxes = await MailboxNamesAsync(export.TenantId, hits.Select(h => h.MailboxId).Distinct().ToList(), ct);
        var manifest = new StringBuilder("﻿mailbox,message_id,rfc_message_id,received_at,from,subject,hidden_from_user,file,sha256\r\n");
        var written = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in hits.GroupBy(h => h.MailboxId))
        {
            var mailboxName = mailboxes.GetValueOrDefault(group.Key, group.Key.ToString());
            var ids = group.Select(h => h.MessageId).ToList();
            var rows = await db.Messages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == export.TenantId && ids.Contains(m.Id) && m.ExpungedAt == null)
                .Select(m => new { m.Id, m.BlobSha256, m.FromAddr, m.ReceivedAt, m.RfcMessageId, m.Subject }).ToListAsync(ct);
            var byId = rows.ToDictionary(r => r.Id);
            Stream? mbox = null;
            var file = FileName(mailboxName) + ".mbox";
            if (export.Format == "MBOX")
            {
                mbox = zip.CreateEntry(Unique(names, FileName(mailboxName)) + ".mbox", CompressionLevel.Fastest).Open();
            }

            try
            {
                foreach (var hit in group.OrderBy(h => h.ReceivedAt))
                {
                    if (!byId.TryGetValue(hit.MessageId, out var m))
                    {
                        continue;
                    }

                    using var raw = new MemoryStream();
                    await blobs.ReadAsync(export.TenantId, m.BlobSha256, raw, ct: ct);
                    var sha = Convert.ToHexStringLower(SHA256.HashData(raw.ToArray()));
                    raw.Position = 0;
                    if (mbox is not null)
                    {
                        await Mbox.WriteMessageAsync(mbox, m.FromAddr, m.ReceivedAt, raw, ct);
                    }
                    else
                    {
                        file = FileName(mailboxName) + "/" + m.ReceivedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + m.Id.ToString("N")[..12] + ".eml";
                        await using var eml = zip.CreateEntry(file, CompressionLevel.Fastest).Open();
                        await raw.CopyToAsync(eml, ct);
                    }

                    manifest.Append(string.Join(',', new[]
                    {
                        mailboxName, m.Id.ToString(), m.RfcMessageId ?? "", m.ReceivedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture), m.FromAddr ?? "",
                        m.Subject ?? "", hit.HiddenFromUser ? "yes" : "no", file, sha,
                    }.Select(Csv))).Append("\r\n");
                    written++;
                    await tick();
                }
            }
            finally
            {
                if (mbox is not null)
                {
                    await mbox.DisposeAsync();
                }
            }
        }

        await TextAsync(zip, "manifest.csv", manifest.ToString(), ct);
        return written;
    }

    /// <summary>The whole tenant at the end of a contract: every user's data, shared mailboxes, users and domains lists.</summary>
    public async Task TenantAsync(ZipArchive zip, Guid tenantId, Func<Task> tick, CancellationToken ct)
    {
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == tenantId && u.DeletedAt == null).OrderBy(u => u.PrimaryEmail).ToListAsync(ct);
        var csv = new StringBuilder("﻿email,name,status,admin,quota_bytes,created_at\r\n");
        foreach (var u in users)
        {
            csv.Append(string.Join(',', new[]
            {
                u.PrimaryEmail, u.DisplayName, u.Status.ToString(), u.IsTenantAdmin ? "yes" : "no", u.QuotaBytes.ToString(CultureInfo.InvariantCulture),
                u.CreatedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
            }.Select(Csv))).Append("\r\n");
            await UserDataAsync(zip, tenantId, u.Id, "users/" + FileName(u.PrimaryEmail) + "/", tick, ct);
        }

        await TextAsync(zip, "users.csv", csv.ToString(), ct);
        var shared = await db.SharedMailboxes.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId && s.DeletedAt == null).ToListAsync(ct);
        foreach (var s in shared)
        {
            var mb = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == tenantId && m.OwnerId == s.Id && m.DeletedAt == null)
                .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
            if (mb is { } id)
            {
                await MailboxAsync(zip, tenantId, id, "shared/" + FileName(s.Email) + "/", tick, ct);
            }
        }

        var domains = await db.Domains.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenantId && d.DeletedAt == null).Select(d => d.Domain).ToListAsync(ct);
        await TextAsync(zip, "domains.txt", string.Join("\n", domains) + "\n", ct);
    }

    private async Task<Dictionary<Guid, string>> MailboxNamesAsync(Guid tenantId, List<Guid> ids, CancellationToken ct)
    {
        var rows = await (from m in db.Mailboxes.IgnoreQueryFilters()
                          where m.TenantId == tenantId && ids.Contains(m.Id)
                          select new
                          {
                              m.Id,
                              Email = db.Users.IgnoreQueryFilters().Where(u => u.Id == m.OwnerId).Select(u => u.PrimaryEmail).FirstOrDefault()
                                      ?? db.SharedMailboxes.IgnoreQueryFilters().Where(s => s.Id == m.OwnerId).Select(s => s.Email).FirstOrDefault(),
                          }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Email ?? r.Id.ToString());
    }

    public static async Task TextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    private static string LabelPath(LabelSummary l, IReadOnlyList<LabelSummary> all)
    {
        var parts = new List<string> { l.SystemKey ?? l.Name };
        var parent = l.ParentId;
        for (var guard = 0; parent is { } p && guard < 20; guard++)
        {
            var pl = all.FirstOrDefault(x => x.Id == p);
            if (pl is null)
            {
                break;
            }

            parts.Insert(0, pl.Name);
            parent = pl.ParentId;
        }

        return string.Join(" - ", parts);
    }

    /// <summary>A file name safe on Windows, macOS and Linux; Vietnamese letters kept.</summary>
    internal static string FileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s.Length > 120 ? s[..120] : s;
    }

    private static string Unique(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return candidate;
    }

    internal static string Csv(string v)
    {
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }

        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
