using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueMail.Application.Common;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Compliance;
using BlueMail.Domain.Identity;
using BlueMail.Domain.Migration;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Infrastructure.Security;
using BlueMail.Protocols.Imap;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Migration;

public sealed record ImportJobDto(Guid Id, Guid UserId, string? UserEmail, string Source, string? Host, string? Username, string? FileName, string Status,
    int Total, int Imported, int Skipped, int Failed, string? CurrentFolder, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, Guid? BatchId);

public sealed record ImapSourceDto(string Host, int Port, string Tls, string Username, string Password);

/// <summary>Who an import is for: the caller, or (with mail.admin.migration.edit) another user of the tenant.</summary>
internal static class ImportTarget
{
    public static async Task<User> ResolveAsync(BlueMailDbContext db, ICurrentUser me, Guid? userId, CancellationToken ct)
    {
        if (userId is { } id && id != me.UserId)
        {
            me.Require(Permissions.AdminMigrationEdit);
        }

        var target = userId ?? me.UserId;
        return await db.Users.FirstOrDefaultAsync(u => u.Id == target && u.DeletedAt == null, ct) ?? throw AppException.NotFound("user_not_found");
    }

    public static ImportJobDto Dto(ImportJob j, string? email) => new(j.Id, j.UserId, email, EnumNames.ToUpperSnake(j.Source.ToString()), j.Host, j.Username, j.FileName,
        EnumNames.ToUpperSnake(j.Status.ToString()), j.Total, j.Imported, j.Skipped, j.Failed, j.CurrentFolder, j.Error, j.CreatedAt, j.FinishedAt, j.BatchId);

    public static async Task<Guid> SaveAsync(BlueMailDbContext db, ImportJob job, CancellationToken ct)
    {
        db.ImportJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw AppException.Conflict("import_running"); // one import at a time per mailbox (DB rule)
        }

        return job.Id;
    }
}

public sealed class ImapSourceValidator : AbstractValidator<ImapSourceDto>
{
    public ImapSourceValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(253);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Tls).Must(t => t is "IMPLICIT" or "STARTTLS").WithErrorCode("invalid_option");
        RuleFor(x => x.Username).NotEmpty().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(1000);
    }
}

// ---- test a source before starting ----

public sealed record TestImapSourceCommand(ImapSourceDto Source) : IRequest<ImapTestResult>;

public sealed record ImapTestResult(bool Ok, string? Error, int Folders, long Messages, bool Gmail);

public sealed class TestImapSourceValidator : AbstractValidator<TestImapSourceCommand>
{
    public TestImapSourceValidator() => RuleFor(x => x.Source).SetValidator(new ImapSourceValidator());
}

public sealed class TestImapSourceHandler(ImportOptions options) : IRequestHandler<TestImapSourceCommand, ImapTestResult>
{
    public async Task<ImapTestResult> Handle(TestImapSourceCommand c, CancellationToken ct)
    {
        var s = c.Source;
        var (address, error) = await options.CheckAsync(s.Host.Trim(), s.Port, ct);
        if (address is null)
        {
            return new ImapTestResult(false, error, 0, 0, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await using var client = new ImapClient(options.Client(s.Tls) with { ConnectTimeout = TimeSpan.FromSeconds(15), CommandTimeout = TimeSpan.FromSeconds(30) });
            await client.ConnectAsync(s.Host.Trim(), address, s.Port, timeout.Token);
            await client.LoginAsync(s.Username.Trim(), s.Password, timeout.Token);
            var folders = (await client.ListAsync(timeout.Token)).Where(f => f.Selectable).ToList();
            long messages = 0;
            foreach (var f in folders.Where(f => !client.IsGmail || f.SpecialUse is "\\ALL" or "\\JUNK" or "\\TRASH"))
            {
                messages += (await client.ExamineAsync(f, timeout.Token)).Exists;
            }

            var gmail = client.IsGmail;
            await client.LogoutAsync(timeout.Token);
            return new ImapTestResult(true, null, folders.Count, messages, gmail);
        }
        catch (ImapCommandException e) when (e.IsAuthenticationFailure || e.Response.Status == ImapStatus.No)
        {
            return new ImapTestResult(false, "import_auth_failed", 0, 0, false);
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            return new ImapTestResult(false, "import_tls_failed", 0, 0, false);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ImapProtocolException or OperationCanceledException)
        {
            return new ImapTestResult(false, "import_connection_failed", 0, 0, false);
        }
    }
}

// ---- start ----

public sealed record StartImapImportCommand(Guid? UserId, ImapSourceDto Source) : IRequest<Guid>;

public sealed class StartImapImportValidator : AbstractValidator<StartImapImportCommand>
{
    public StartImapImportValidator() => RuleFor(x => x.Source).SetValidator(new ImapSourceValidator());
}

public sealed class StartImapImportHandler(BlueMailDbContext db, ICurrentUser me, ImportOptions options, TenantKeyProvider keys, AuditWriter audit)
    : IRequestHandler<StartImapImportCommand, Guid>
{
    public async Task<Guid> Handle(StartImapImportCommand c, CancellationToken ct)
    {
        var user = await ImportTarget.ResolveAsync(db, me, c.UserId, ct);
        var s = c.Source;
        if ((await options.CheckAsync(s.Host.Trim(), s.Port, ct)).Error is { } error)
        {
            throw AppException.BadRequest(error);
        }

        var job = new ImportJob
        {
            Id = Guid.NewGuid(), TenantId = user.TenantId, UserId = user.Id, RequestedBy = me.UserId, Source = ImportSource.Imap, Host = s.Host.Trim().ToLowerInvariant(),
            Port = s.Port, Tls = s.Tls, Username = s.Username.Trim(), Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        job.PasswordCipher = await keys.EncryptAsync(user.TenantId, "import-password", job.Id, Encoding.UTF8.GetBytes(s.Password), ct);
        await ImportTarget.SaveAsync(db, job, ct);
        await audit.WriteAsync(new AuditEntry("import.start", AuditResult.Success, user.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", user.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { source = "IMAP", job.Host, job.Username })), ct);
        return job.Id;
    }
}

public sealed record StartFileImportCommand(Guid? UserId, Guid UploadId) : IRequest<Guid>;

public sealed class StartFileImportHandler(BlueMailDbContext db, ICurrentUser me, AuditWriter audit) : IRequestHandler<StartFileImportCommand, Guid>
{
    public async Task<Guid> Handle(StartFileImportCommand c, CancellationToken ct)
    {
        var user = await ImportTarget.ResolveAsync(db, me, c.UserId, ct);
        // The file is the caller's own upload, complete.
        var up = await db.Uploads.FirstOrDefaultAsync(u => u.Id == c.UploadId && u.UserId == me.UserId && u.DeletedAt == null, ct)
                 ?? throw AppException.NotFound("upload_not_found");
        if (up.Offset != up.Length)
        {
            throw AppException.BadRequest("upload_incomplete");
        }

        var ext = Path.GetExtension(up.FileName).ToLowerInvariant();
        var source = ext switch
        {
            ".mbox" or ".mbx" or "" => ImportSource.Mbox,
            ".eml" or ".zip" => ImportSource.Eml,
            _ => throw AppException.BadRequest("import_file_type"),
        };
        var job = new ImportJob
        {
            Id = Guid.NewGuid(), TenantId = user.TenantId, UserId = user.Id, RequestedBy = me.UserId, Source = source, UploadId = up.Id, UploadChunks = up.Chunks,
            FileName = up.FileName, Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        // The upload must outlive the job: imports of large files can wait in the queue.
        up.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        await ImportTarget.SaveAsync(db, job, ct);
        await audit.WriteAsync(new AuditEntry("import.start", AuditResult.Success, user.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "user", user.Id.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { source = job.Source.ToString(), up.FileName })), ct);
        return job.Id;
    }
}

// ---- batch (administrators, CSV) ----

public sealed record ImportBatchRow(string Email, string Host, int Port, string Tls, string Username, string Password);

public sealed record ImportBatchResult(Guid? BatchId, int Created, IReadOnlyList<Admin.Users.ImportRowError> Errors);

public sealed record StartImportBatchCommand(string Csv) : IRequest<ImportBatchResult>;

public sealed class StartImportBatchHandler(BlueMailDbContext db, ICurrentUser me, ImportOptions options, TenantKeyProvider keys, AuditWriter audit)
    : IRequestHandler<StartImportBatchCommand, ImportBatchResult>
{
    public const int MaxRows = 2000;

    public async Task<ImportBatchResult> Handle(StartImportBatchCommand c, CancellationToken ct)
    {
        me.Require(Permissions.AdminMigrationEdit);
        var rows = Parse(c.Csv);
        if (rows.Count is 0 or > MaxRows)
        {
            throw AppException.BadRequest("import_size");
        }

        var errors = new List<Admin.Users.ImportRowError>();
        var users = new Dictionary<int, User>();
        var validator = new ImapSourceValidator();
        for (var i = 0; i < rows.Count; i++)
        {
            var n = i + 1;
            var r = rows[i];
            var email = r.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.PrimaryEmail == email && u.DeletedAt == null, ct);
            if (user is null)
            {
                errors.Add(new(n, "email", "user_not_found"));
                continue;
            }

            if (rows.Take(i).Any(o => o.Email.Trim().Equals(email, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new(n, "email", "duplicate_in_file"));
                continue;
            }

            foreach (var f in (await validator.ValidateAsync(new ImapSourceDto(r.Host, r.Port, r.Tls, r.Username, r.Password), ct)).Errors)
            {
                errors.Add(new(n, char.ToLowerInvariant(f.PropertyName[0]) + f.PropertyName[1..], f.ErrorCode == "NotEmptyValidator" ? "required" : "invalid_format"));
            }

            if ((await options.CheckAsync(r.Host.Trim(), r.Port, ct)).Error is { } hostError)
            {
                errors.Add(new(n, "host", hostError));
            }

            users[i] = user;
        }

        if (errors.Count > 0)
        {
            return new ImportBatchResult(null, 0, errors);
        }

        var batch = Guid.NewGuid();
        var created = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var user = users[i];
            var job = new ImportJob
            {
                Id = Guid.NewGuid(), TenantId = user.TenantId, UserId = user.Id, RequestedBy = me.UserId, BatchId = batch, Source = ImportSource.Imap,
                Host = r.Host.Trim().ToLowerInvariant(), Port = r.Port, Tls = r.Tls, Username = r.Username.Trim(), Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
            };
            job.PasswordCipher = await keys.EncryptAsync(user.TenantId, "import-password", job.Id, Encoding.UTF8.GetBytes(r.Password), ct);
            db.ImportJobs.Add(job);
            created++;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw AppException.Conflict("import_running");
        }

        await tx.CommitAsync(ct);
        await audit.WriteAsync(new AuditEntry("import.batch", AuditResult.Success, me.TenantId, me.UserId, me.Email, me.Ip, me.UserAgent, "batch", batch.ToString(),
            NewValueJson: JsonSerializer.Serialize(new { rows = created })), ct);
        return new ImportBatchResult(batch, created, []);
    }

    /// <summary>CSV with a header: email, host, port, tls (IMPLICIT/STARTTLS, optional), username, password.</summary>
    internal static List<ImportBatchRow> Parse(string csv)
    {
        var lines = Contacts.ContactCsv.Parse(csv.TrimStart('﻿'));
        if (lines.Count < 2)
        {
            return [];
        }

        var header = lines[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int email = Col("email", "user", "người dùng"), host = Col("host", "server", "máy chủ"), port = Col("port", "cổng"), tls = Col("tls", "security"),
            user = Col("username", "login", "tên đăng nhập"), pass = Col("password", "mật khẩu");
        string Cell(IReadOnlyList<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
        return lines.Skip(1).Where(r => r.Any(x => x.Trim().Length > 0)).Select(r => new ImportBatchRow(
            Cell(r, email), Cell(r, host), int.TryParse(Cell(r, port), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 993,
            Cell(r, tls).ToUpperInvariant() is "STARTTLS" ? "STARTTLS" : "IMPLICIT", Cell(r, user), Cell(r, pass))).ToList();
    }
}

// ---- list, errors, cancel ----

public sealed record ListImportsQuery(bool Admin, Guid? BatchId) : IRequest<IReadOnlyList<ImportJobDto>>;

public sealed class ListImportsHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ListImportsQuery, IReadOnlyList<ImportJobDto>>
{
    public async Task<IReadOnlyList<ImportJobDto>> Handle(ListImportsQuery q, CancellationToken ct)
    {
        var jobs = db.ImportJobs.AsNoTracking();
        if (q.Admin)
        {
            me.Require(Permissions.AdminMigrationEdit);
            if (q.BatchId is { } b)
            {
                jobs = jobs.Where(j => j.BatchId == b);
            }
        }
        else
        {
            jobs = jobs.Where(j => j.UserId == me.UserId);
        }

        var list = await (from j in jobs
                          join u in db.Users on j.UserId equals u.Id
                          orderby j.CreatedAt descending
                          select new { j, u.PrimaryEmail }).Take(500).ToListAsync(ct);
        return list.Select(x => ImportTarget.Dto(x.j, x.PrimaryEmail)).ToList();
    }
}

internal static class ImportAccess
{
    /// <summary>The job if the caller may see it: its owner, who started it, or a migration administrator.</summary>
    public static async Task<ImportJob> FindAsync(BlueMailDbContext db, ICurrentUser me, Guid id, CancellationToken ct)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw AppException.NotFound("import_not_found");
        if (job.UserId != me.UserId && job.RequestedBy != me.UserId && !me.Permissions.Contains(Permissions.AdminMigrationEdit))
        {
            throw AppException.NotFound("import_not_found");
        }

        return job;
    }
}

public sealed record ImportErrorsCsvQuery(Guid JobId) : IRequest<byte[]>;

public sealed class ImportErrorsCsvHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<ImportErrorsCsvQuery, byte[]>
{
    private static readonly string[] Header = ["time", "folder", "uid", "code", "problem"];

    public async Task<byte[]> Handle(ImportErrorsCsvQuery q, CancellationToken ct)
    {
        var job = await ImportAccess.FindAsync(db, me, q.JobId, ct);
        var rows = await db.ImportErrors.AsNoTracking().Where(e => e.JobId == job.Id).OrderBy(e => e.Id).Take(100_000).ToListAsync(ct);
        var culture = CultureInfo.CurrentUICulture;
        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(',', Header.Select(Csv))).Append("\r\n");
        foreach (var e in rows)
        {
            sb.Append(string.Join(',', new[]
            {
                e.OccurredAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture), e.Folder ?? "", e.Uid?.ToString(CultureInfo.InvariantCulture) ?? "", e.Code,
                Infrastructure.Localization.JsonStringLocalizerFactory.Get("import.error." + e.Code, culture),
            }.Select(Csv))).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>CSV cell; formulas are neutralised (a spreadsheet must not run text from an import as a formula).</summary>
    private static string Csv(string v)
    {
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }

        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed record CancelImportCommand(Guid JobId) : IRequest<Unit>;

public sealed class CancelImportHandler(BlueMailDbContext db, ICurrentUser me) : IRequestHandler<CancelImportCommand, Unit>
{
    public async Task<Unit> Handle(CancelImportCommand c, CancellationToken ct)
    {
        var job = await ImportAccess.FindAsync(db, me, c.JobId, ct);
        var n = await db.ImportJobs.Where(j => j.Id == job.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)).ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Status, JobStatus.Cancelled).SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow).SetProperty(x => x.PasswordCipher, (byte[]?)null), ct);
        return n == 0 ? throw AppException.Conflict("import_finished") : Unit.Value;
    }
}
