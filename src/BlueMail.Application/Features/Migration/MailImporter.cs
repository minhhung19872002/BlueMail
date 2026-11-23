using System.Security.Cryptography;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Migration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Scanner;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Migration;

/// <summary>One message to import: where it came from, its bytes (seekable) and what the source said about it.</summary>
public sealed record ImportItem(
    string SourceKey,
    Stream Content,
    DateTimeOffset? Date,
    IReadOnlyList<string> SystemLabels,
    IReadOnlyList<IReadOnlyList<string>> UserLabels,
    bool Seen,
    bool Answered = false,
    bool Forwarded = false,
    IReadOnlyList<string>? Keywords = null);

public enum ImportOutcome { Imported, Skipped, Merged, Failed }

/// <summary>
/// Imports one message into a mailbox (PROMPT 7.7): skipped when this source position was imported before; when the
/// same bytes are already there (another folder of the same account) the message only gains the new labels; otherwise
/// it is scanned and stored with its date and flags. No filters, auto-replies or forwarding run for imported mail.
/// </summary>
public sealed class MailImporter(BlueMailDbContext db, PgMailStore store, IFileScanner scanner)
{
    /// <summary>Largest message imported; larger ones are reported, not stored (the web send limit is 50 MB).</summary>
    public const long MaxMessageBytes = 150L * 1024 * 1024;

    public async Task<(ImportOutcome Outcome, string? Code)> ImportAsync(MailboxAccess access, ImportItem item, ImportLabels labels, CancellationToken ct)
    {
        if (await db.ImportedMessages.IgnoreQueryFilters().AnyAsync(x => x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.SourceKey == item.SourceKey, ct))
        {
            return (ImportOutcome.Skipped, null);
        }

        if (item.Content.Length > MaxMessageBytes)
        {
            return (ImportOutcome.Failed, "too_large");
        }

        item.Content.Position = 0;
        var sha = await SHA256.HashDataAsync(item.Content, ct);
        item.Content.Position = 0;
        var labelIds = new List<Guid>();
        foreach (var path in item.UserLabels)
        {
            labelIds.Add(await labels.ResolveAsync(path, ct));
        }

        var existing = await (from x in db.ImportedMessages.IgnoreQueryFilters()
                              join m in db.Messages.IgnoreQueryFilters() on x.MessageId equals m.Id
                              where x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.ContentSha256 == sha && m.ExpungedAt == null && m.DeletedAt == null
                              select (Guid?)x.MessageId).FirstOrDefaultAsync(ct);
        if (existing is { } same)
        {
            // A copy of a message already imported (the same mail in two folders): one message, both labels.
            var add = item.SystemLabels.Where(l => l is not (SystemLabels.Starred or SystemLabels.Important)).ToList();
            await store.ApplyAsync(access, [same], new MessageChange
            {
                AddSystem = add, AddLabels = labelIds,
                Starred = item.SystemLabels.Contains(SystemLabels.Starred) ? true : null,
                Important = item.SystemLabels.Contains(SystemLabels.Important) ? true : null,
            }, ct);
            await RecordAsync(access, item.SourceKey, sha, same, ct);
            return (ImportOutcome.Merged, null);
        }

        MimeMessage parsed;
        try
        {
            parsed = MimeMessage.Parse(item.Content);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException)
        {
            return (ImportOutcome.Failed, "unreadable");
        }

        if (parsed.Headers.Count == 0)
        {
            return (ImportOutcome.Failed, "unreadable");
        }

        // Imported files go through the same scanner as mail from the Internet; dangerous content is not stored.
        var statuses = new Dictionary<string, VirusStatus>(StringComparer.Ordinal);
        foreach (var part in parsed.Root.Walk().Where(p => p.IsLeaf && (p.IsAttachment || p.FileName is not null || !p.MediaType.StartsWith("text/", StringComparison.Ordinal))))
        {
            await using var body = part.OpenDecodedBody(item.Content);
            var result = await scanner.ScanAsync(body, part.FileName, ct);
            if (result.Status == ScanStatus.Malicious)
            {
                return (ImportOutcome.Failed, "malicious");
            }

            statuses[part.PartPath] = result.Status == ScanStatus.Suspicious ? VirusStatus.Suspicious : VirusStatus.Clean;
        }

        item.Content.Position = 0;
        var facts = MessageFacts.Extract(parsed, item.Content);
        item.Content.Position = 0;
        var system = item.SystemLabels.Distinct().ToList();
        var delivered = await store.DeliverAsync(new DeliveryRequest(access.TenantId, item.Content, facts,
            [
                new MailboxDelivery(access.MailboxId, system, Seen: item.Seen, Important: system.Contains(SystemLabels.Important), ExtraLabelIds: labelIds,
                    Draft: system.Contains(SystemLabels.Drafts))
                {
                    Answered = item.Answered, Forwarded = item.Forwarded, Keywords = item.Keywords ?? [],
                },
            ],
            item.Date ?? facts.Date ?? DateTimeOffset.UtcNow, AttachmentStatus: statuses), ct);
        await RecordAsync(access, item.SourceKey, sha, delivered[0].MessageId, ct);
        return (ImportOutcome.Imported, null);
    }

    private async Task RecordAsync(MailboxAccess access, string sourceKey, byte[] sha, Guid messageId, CancellationToken ct)
    {
        db.ImportedMessages.Add(new ImportedMessage
        {
            TenantId = access.TenantId, MailboxId = access.MailboxId, SourceKey = sourceKey, ContentSha256 = sha, MessageId = messageId, CreatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another run recorded it first.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>Finds or creates the user labels of a mailbox by path ("Dự án" / "Q4"), once per path per import.</summary>
public sealed class ImportLabels(PgMailStore store, MailboxAccess access)
{
    private Dictionary<(Guid? Parent, string Name), Guid>? _known;

    public async Task<Guid> ResolveAsync(IReadOnlyList<string> path, CancellationToken ct)
    {
        _known ??= (await store.GetLabelsAsync(access, ct)).Where(l => l.SystemKey is null)
            .GroupBy(l => (l.ParentId, l.Name.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First().Id);
        Guid? parent = null;
        foreach (var raw in path)
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            name = name.Length > 100 ? name[..100] : name;
            var key = (parent, name.ToLowerInvariant());
            if (!_known.TryGetValue(key, out var id))
            {
                try
                {
                    id = await store.CreateLabelAsync(access, name, parent, null, null, ct);
                }
                catch (LabelConflictException)
                {
                    id = (await store.GetLabelsAsync(access, ct)).First(l => l.SystemKey is null && l.ParentId == parent && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Id;
                }

                _known[key] = id;
            }

            parent = id;
        }

        return parent ?? throw new ArgumentException("Empty label path.", nameof(path));
    }
}
