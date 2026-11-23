using Amazon.S3;
using Amazon.S3.Model;
using BlueMail.Scanner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Scanning;

/// <summary>
/// Keeps the scanner's data fresh (PROMPT 2.6.4, docs/00 Z7):
/// - layer 7: MalwareBazaar SHA-256 export (CC0), downloaded daily with the free abuse.ch Auth-Key
///   (BM_MALWAREBAZAAR_KEY). Without a key the layer is off and a warning is logged; scanning goes on with the other
///   layers. Each good download is copied to S3 (bm-backups/scanner/) so a restart loads it without the network.
/// - layer 8: operators' rule files (*.rules) in BM_SCANNER_RULES_DIR, reloaded when they change. A file with a
///   syntax error is rejected as a whole and logged; the previous rules stay active.
/// </summary>
public sealed class ScannerFeeds(MalwareHashList hashes, PatternRuleScanner patterns, IAmazonS3 s3, IConfiguration config, ILogger<ScannerFeeds> log)
    : BackgroundService
{
    public const string FullExportUrl = "https://bazaar.abuse.ch/export/txt/sha256/full/";
    public const string BackupBucket = "bm-backups";
    public const string BackupKey = "scanner/malwarebazaar-sha256.zip";
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    private DateTime _rulesStamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadBackupAsync(stoppingToken);
        var nextHashes = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            ReloadRules();
            if (DateTimeOffset.UtcNow >= nextHashes)
            {
                nextHashes = DateTimeOffset.UtcNow + (await DownloadAsync(stoppingToken) ? Refresh : TimeSpan.FromHours(1));
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LoadBackupAsync(CancellationToken ct)
    {
        try
        {
            using var obj = await s3.GetObjectAsync(BackupBucket, BackupKey, ct);
            using var ms = new MemoryStream();
            await obj.ResponseStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            var (set, _) = MalwareHashList.Parse(ms);
            hashes.Replace(set);
            log.LogInformation("Scanner layer 7: loaded {Count} known-malware hashes from the S3 copy", set.Count);
        }
        catch (Exception e) when (e is AmazonS3Exception or HttpRequestException or InvalidDataException)
        {
            log.LogInformation("Scanner layer 7: no S3 copy of the hash list yet ({Reason})", e.Message);
        }
    }

    private async Task<bool> DownloadAsync(CancellationToken ct)
    {
        var key = config["BM_MALWAREBAZAAR_KEY"];
        if (string.IsNullOrWhiteSpace(key))
        {
            if (hashes.Count == 0)
            {
                log.LogWarning("Scanner layer 7 (known-malware hashes) is off: BM_MALWAREBAZAAR_KEY is not set (docs/00 Z7)");
            }

            return true; // nothing to retry until configuration changes
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, FullExportUrl);
            req.Headers.Add("Auth-Key", key);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var (set, rejected) = MalwareHashList.Parse(new MemoryStream(bytes));
            if (set.Count < 1000)
            {
                // A real export has hundreds of thousands of entries: a tiny one is an error page, keep the old list.
                log.LogWarning("Scanner layer 7: download had only {Count} hashes; keeping the current list", set.Count);
                return false;
            }

            hashes.Replace(set);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = BackupBucket, Key = BackupKey, InputStream = new MemoryStream(bytes), UseChunkEncoding = false }, ct);
            log.LogInformation("Scanner layer 7: {Count} known-malware hashes loaded ({Rejected} malformed lines skipped)", set.Count, rejected);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or AmazonS3Exception)
        {
            log.LogWarning(e, "Scanner layer 7: download failed; keeping the current list ({Count} hashes)", hashes.Count);
            return false;
        }
    }

    private void ReloadRules()
    {
        var dir = config["BM_SCANNER_RULES_DIR"];
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var files = Directory.GetFiles(dir, "*.rules").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var stamp = files.Count == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
        if (stamp == _rulesStamp)
        {
            return;
        }

        _rulesStamp = stamp;
        var merged = PatternRuleSet.Empty;
        foreach (var f in files)
        {
            try
            {
                merged = merged.Merge(PatternRuleSet.Parse(File.ReadAllText(f), Path.GetFileName(f)));
            }
            catch (FormatException e)
            {
                log.LogError("Scanner layer 8: {File} rejected: {Error}", Path.GetFileName(f), e.Message);
            }
        }

        patterns.Replace(merged);
        log.LogInformation("Scanner layer 8: {Count} rules active ({Operator} from {Dir})", patterns.Rules.Rules.Count, merged.Rules.Count, dir);
    }
}
