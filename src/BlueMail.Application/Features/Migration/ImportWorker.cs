using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Domain.Compliance;
using BlueMail.Domain.Mail;
using BlueMail.Domain.Migration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Migration;

/// <summary>
/// Runs import jobs in the background (PROMPT 8: long work never runs inside an HTTP request). A job is claimed with a
/// lease; a crashed or restarted worker leaves the lease to expire and the job is picked up again - the dedup rows make
/// the second run import nothing twice.
/// </summary>
public sealed class ImportWorker(IServiceScopeFactory scopes, ILogger<ImportWorker> log) : BackgroundService
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    internal const int FetchBatch = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Import loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>Claims and runs one job; false when there was none.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE migration.import_jobs SET status = 'RUNNING', started_at = coalesce(started_at, now()), locked_until = now() + {Lease}
            WHERE id = (SELECT id FROM migration.import_jobs
                        WHERE status = 'QUEUED' OR (status = 'RUNNING' AND locked_until < now())
                        ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id AS value
            """).ToListAsync(ct);
        if (claimed.Count == 0)
        {
            return false;
        }

        var job = await db.ImportJobs.IgnoreQueryFilters().AsNoTracking().FirstAsync(j => j.Id == claimed[0], ct);
        var run = new ImportRun(scope.ServiceProvider, job);
        string? error = null;
        try
        {
            await run.ExecuteAsync(ct);
        }
        catch (ImportCancelledException)
        {
            return true;
        }
        catch (ImapCommandException ex) when (ex.IsAuthenticationFailure)
        {
            error = "import_auth_failed";
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ImapProtocolException or System.Security.Authentication.AuthenticationException)
        {
            log.LogWarning(ex, "Import {Job} lost its source", job.Id);
            error = ex is System.Security.Authentication.AuthenticationException ? "import_tls_failed" : "import_connection_failed";
        }
        catch (ImportFailedException ex)
        {
            error = ex.Code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Import {Job} failed", job.Id);
            error = "import_failed";
        }

        await run.FinishAsync(error, CancellationToken.None);
        return true;
    }
}

internal sealed class ImportCancelledException : Exception;

internal sealed class ImportFailedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>One execution of a job: counters, heartbeat, errors, and the source-specific loops.</summary>
internal sealed class ImportRun(IServiceProvider sp, ImportJob job)
{
    private readonly BlueMailDbContext _db = sp.GetRequiredService<BlueMailDbContext>();
    private readonly PgMailStore _store = sp.GetRequiredService<PgMailStore>();
    private readonly MailImporter _importer = sp.GetRequiredService<MailImporter>();
    private int _imported;
    private int _skipped;
    private int _failed;
    private int _total;
    private string? _folder;
    private DateTimeOffset _lastBeat = DateTimeOffset.UtcNow;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var mailboxId = await _db.Mailboxes.IgnoreQueryFilters()
            .Where(m => m.TenantId == job.TenantId && m.OwnerId == job.UserId && m.DeletedAt == null).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct)
            ?? throw new ImportFailedException("import_no_mailbox");
        var access = PgMailStore.SystemAccess(job.TenantId, mailboxId);
        var labels = new ImportLabels(_store, access);
        switch (job.Source)
        {
            case ImportSource.Imap:
                await RunImapAsync(access, labels, ct);
                break;
            default:
                await RunFileAsync(access, labels, ct);
                break;
        }
    }

    // ---- IMAP ----

    private async Task RunImapAsync(MailboxAccess access, ImportLabels labels, CancellationToken ct)
    {
        var options = sp.GetRequiredService<ImportOptions>();
        var (address, error) = await options.CheckAsync(job.Host!, job.Port, ct);
        if (address is null)
        {
            throw new ImportFailedException(error!);
        }

        var keys = sp.GetRequiredService<TenantKeyProvider>();
        var password = Encoding.UTF8.GetString(await keys.DecryptAsync(job.TenantId, "import-password", job.Id, job.PasswordCipher ?? throw new ImportFailedException("import_failed"), ct));
        await using var client = new ImapClient(options.Client(job.Tls));
        await client.ConnectAsync(job.Host!, address, job.Port, ct);
        await client.LoginAsync(job.Username!, password, ct);
        var folders = (await client.ListAsync(ct)).Where(f => f.Selectable).ToList();

        // Gmail shows every message in [Gmail]/All Mail with its labels; Spam and Trash are the only mail outside it.
        var gmail = client.IsGmail;
        var sources = gmail
            ? folders.Where(f => f.SpecialUse is "\\ALL" or "\\JUNK" or "\\TRASH").ToList()
            : folders.Where(f => f.SpecialUse != "\\ALL" || folders.Count == 1).ToList();

        var plan = new List<(ImapFolder Folder, ImapSelected Selected)>();
        foreach (var f in sources)
        {
            var s = await client.ExamineAsync(f, ct);
            plan.Add((f, s));
            _total += (int)Math.Min(s.Exists, int.MaxValue);
        }

        await BeatAsync(force: true, ct);
        var prefix = $"imap:{job.Host!.ToLowerInvariant()}:{job.Username!.ToLowerInvariant()}:";
        foreach (var (folder, _) in plan)
        {
            _folder = folder.Name;
            var selected = await client.ExamineAsync(folder, ct);
            var folderKey = $"{prefix}{folder.RawName}:{selected.UidValidity}:";
            var uids = await client.UidSearchAsync("ALL", ct);
            var done = await DoneUidsAsync(access, folderKey, ct);
            _skipped += uids.Count(done.Contains);
            var todo = uids.Where(u => !done.Contains(u)).ToList();

            // Sizes first: a message too large for BlueMail is reported, not fetched.
            var sizes = new Dictionary<uint, long>();
            await client.UidFetchAsync(todo, withBody: false, m =>
            {
                sizes[m.Uid] = m.Size ?? 0;
                return Task.CompletedTask;
            }, ct);
            foreach (var big in todo.Where(u => sizes.GetValueOrDefault(u) > MailImporter.MaxMessageBytes))
            {
                await ErrorAsync(folder.Name, big, "too_large", null, ct);
            }

            var (folderSystem, folderUser) = ImportMapping.Folder(folder);
            foreach (var batch in todo.Where(u => sizes.GetValueOrDefault(u) <= MailImporter.MaxMessageBytes).Chunk(ImportWorker.FetchBatch))
            {
                await client.UidFetchAsync(batch, withBody: true, async m =>
                {
                    var flags = ImportMapping.Flags(m.Flags);
                    if (flags.Deleted)
                    {
                        _skipped++; // marked for deletion on the source: not carried over
                        return;
                    }

                    var system = new List<string>();
                    var user = new List<IReadOnlyList<string>>();
                    if (gmail)
                    {
                        var (s, u) = ImportMapping.GmailLabels(m.GmailLabels);
                        system.AddRange(s);
                        user.AddRange(u);
                    }

                    if (folderSystem is not null)
                    {
                        system.Add(folderSystem);
                    }

                    if (folderUser is not null && !gmail)
                    {
                        user.Add(folderUser);
                    }

                    if (flags.Flagged)
                    {
                        system.Add(SystemLabels.Starred);
                    }

                    await using var content = m.Body!.OpenRead();
                    await ImportOneAsync(access, labels, folder.Name, m.Uid, new ImportItem(folderKey + m.Uid, content, m.InternalDate, system, user,
                        flags.Seen, flags.Answered, flags.Forwarded, flags.Keywords), ct);
                }, ct);
                await BeatAsync(force: false, ct);
            }
        }

        await client.LogoutAsync(ct);
    }

    private async Task<HashSet<uint>> DoneUidsAsync(MailboxAccess access, string folderKey, CancellationToken ct)
    {
        var keys = await _db.ImportedMessages.IgnoreQueryFilters()
            .Where(x => x.TenantId == access.TenantId && x.MailboxId == access.MailboxId && x.SourceKey.StartsWith(folderKey)).Select(x => x.SourceKey).ToListAsync(ct);
        return keys.Select(k => uint.TryParse(k[folderKey.Length..], out var u) ? u : 0).Where(u => u > 0).ToHashSet();
    }

    // ---- mbox / .eml / .zip of .eml ----

    private async Task RunFileAsync(MailboxAccess access, ImportLabels labels, CancellationToken ct)
    {
        var uploads = sp.GetRequiredService<UploadStore>();
        var path = Path.Combine(Path.GetTempPath(), "bm-import-" + job.Id.ToString("N"));
        try
        {
            byte[] fileSha;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await uploads.CopyToAsync(job.TenantId, job.UploadId ?? throw new ImportFailedException("import_failed"), job.UploadChunks, file, ct);
                file.Position = 0;
                fileSha = await SHA256.HashDataAsync(file, ct);
            }

            var fileKey = "file:" + Convert.ToHexStringLower(fileSha) + ":";
            var name = Path.GetFileNameWithoutExtension(job.FileName ?? "import");
            IReadOnlyList<string> fileLabel = [string.IsNullOrWhiteSpace(name) ? "Import" : name];
            _folder = job.FileName;
            await using var input = File.OpenRead(path);
            if (job.Source == ImportSource.Mbox)
            {
                await foreach (var entry in Mbox.ReadAsync(input, MailImporter.MaxMessageBytes, ct))
                {
                    _total++;
                    if (entry.TooLarge)
                    {
                        await ErrorAsync(job.FileName, entry.Offset, "too_large", null, ct);
                        continue;
                    }

                    await ImportBytesAsync(access, labels, fileKey + entry.Offset, entry.Content, entry.SeparatorDate, fileLabel, entry.Offset, ct);
                }
            }
            else if (IsZip(input))
            {
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)))
                {
                    _total++;
                    if (e.Length > MailImporter.MaxMessageBytes)
                    {
                        await ErrorAsync(e.FullName, null, "too_large", null, ct);
                        continue;
                    }

                    using var ms = new MemoryStream();
                    await using (var s = e.Open())
                    {
                        await s.CopyToAsync(ms, ct);
                    }

                    await ImportBytesAsync(access, labels, fileKey + e.FullName, ms.ToArray(), null, fileLabel, null, ct);
                }
            }
            else
            {
                _total = 1;
                using var ms = new MemoryStream();
                await input.CopyToAsync(ms, ct);
                await ImportBytesAsync(access, labels, fileKey + "0", ms.ToArray(), null, fileLabel, null, ct);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsZip(Stream s)
    {
        Span<byte> head = stackalloc byte[4];
        var n = s.Read(head);
        s.Position = 0;
        return n == 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
    }

    private async Task ImportBytesAsync(MailboxAccess access, ImportLabels labels, string key, byte[] raw, DateTimeOffset? separatorDate,
        IReadOnlyList<string> fileLabel, long? position, CancellationToken ct)
    {
        // mbox files use LF; stored mail uses CRLF (the reader already converted mbox lines).
        var bytes = raw.AsSpan().IndexOf("\r\n"u8) < 0 ? Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(raw).Replace("\n", "\r\n", StringComparison.Ordinal)) : raw;
        MimeMessage parsed;
        try
        {
            parsed = MimeMessage.Parse(bytes);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException)
        {
            await ErrorAsync(job.FileName, position, "unreadable", null, ct);
            return;
        }

        var state = ImportMapping.MboxState(parsed);
        var user = new List<IReadOnlyList<string>>(state.User);
        if (!state.HasLabels)
        {
            user.Add(fileLabel); // no labels in the file: everything under a label named after it
        }

        using var content = new MemoryStream(bytes, writable: false);
        await ImportOneAsync(access, labels, job.FileName, position, new ImportItem(key, content, separatorDate ?? parsed.Date, state.System, user,
            state.Seen, state.Answered, state.Forwarded), ct);
        await BeatAsync(force: false, ct);
    }

    // ---- shared ----

    private async Task ImportOneAsync(MailboxAccess access, ImportLabels labels, string? folder, long? uid, ImportItem item, CancellationToken ct)
    {
        try
        {
            var (outcome, code) = await _importer.ImportAsync(access, item, labels, ct);
            switch (outcome)
            {
                case ImportOutcome.Imported or ImportOutcome.Merged:
                    _imported++;
                    break;
                case ImportOutcome.Skipped:
                    _skipped++;
                    break;
                default:
                    await ErrorAsync(folder, uid, code ?? "import_failed", null, ct);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not ImportCancelledException)
        {
            sp.GetRequiredService<ILogger<ImportWorker>>().LogWarning(e, "Import {Job}: message {Folder}/{Uid} failed", job.Id, folder, uid);
            _db.ChangeTracker.Clear();
            await ErrorAsync(folder, uid, "import_failed", e.GetType().Name, ct);
        }
    }

    private async Task ErrorAsync(string? folder, long? uid, string code, string? detail, CancellationToken ct)
    {
        _failed++;
        _db.ImportErrors.Add(new ImportError
        {
            TenantId = job.TenantId, JobId = job.Id, Folder = folder?.Length > 1000 ? folder[..1000] : folder, Uid = uid, Code = code, Detail = detail,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    /// <summary>Saves progress, renews the lease and stops when the job was cancelled meanwhile.</summary>
    private async Task BeatAsync(bool force, CancellationToken ct)
    {
        if (!force && DateTimeOffset.UtcNow - _lastBeat < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastBeat = DateTimeOffset.UtcNow;
        var status = await _db.ImportJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).Select(j => j.Status).FirstAsync(ct);
        if (status == JobStatus.Cancelled)
        {
            throw new ImportCancelledException();
        }

        await _db.ImportJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Total, _total).SetProperty(x => x.Imported, _imported).SetProperty(x => x.Skipped, _skipped)
            .SetProperty(x => x.Failed, _failed).SetProperty(x => x.CurrentFolder, _folder)
            .SetProperty(x => x.LockedUntil, DateTimeOffset.UtcNow + ImportWorker.Lease), ct);
    }

    public async Task FinishAsync(string? error, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        // The password is kept only while the job can still run (PROMPT 8.1: no secrets lying around).
        await _db.ImportJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id && j.Status == JobStatus.Running).ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Status, error is null ? JobStatus.Done : JobStatus.Failed).SetProperty(x => x.Error, error)
            .SetProperty(x => x.Total, _total).SetProperty(x => x.Imported, _imported).SetProperty(x => x.Skipped, _skipped)
            .SetProperty(x => x.Failed, _failed).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(x => x.PasswordCipher, (byte[]?)null), ct);
    }
}
