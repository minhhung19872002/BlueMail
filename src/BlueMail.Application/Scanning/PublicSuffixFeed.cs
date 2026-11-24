using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using BlueMail.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Scanning;

/// <summary>
/// Keeps the Public Suffix List used for DMARC organizational domains current (PROMPT 2.3, 9 "cập nhật Public Suffix
/// List"). Every process that evaluates DMARC runs it: at start it loads the last good copy from S3 (so a restart does
/// not need the network), then downloads <c>BM_PSL_URL</c> (default publicsuffix.org; "off" disables) once a day.
/// A download is installed only when it parses as a complete list; otherwise the current one stays and a warning is
/// logged. The embedded list is the fallback.
/// </summary>
public sealed class PublicSuffixFeed(IAmazonS3 s3, IConfiguration config, ILogger<PublicSuffixFeed> log) : BackgroundService
{
    public const string DefaultUrl = "https://publicsuffix.org/list/public_suffix_list.dat";
    public const string BackupKey = "psl/public_suffix_list.dat";
    public static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    private DateTimeOffset? _lastModified;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = config["BM_PSL_URL"] is { Length: > 0 } u ? u : DefaultUrl;
        await LoadBackupAsync(stoppingToken);
        if (url.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Public Suffix List updates are off (BM_PSL_URL=off); using {Rules} rules", PublicSuffixList.Default.RuleCount);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAsync(url, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Public Suffix List download failed; keeping the current list");
            }

            await Task.Delay(Refresh, stoppingToken);
        }
    }

    /// <summary>Downloads and installs the list when it changed and looks complete; true when a new list was installed.</summary>
    public async Task<bool> UpdateAsync(string url, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(60);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("BlueMail-PSL/1.0");
        if (_lastModified is { } since)
        {
            req.Headers.IfModifiedSince = since;
        }

        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > PublicSuffixList.MaxBytes)
        {
            log.LogWarning("Public Suffix List download refused: {Bytes} bytes is too large", res.Content.Headers.ContentLength);
            return false;
        }

        var text = await res.Content.ReadAsStringAsync(ct);
        if (PublicSuffixList.TryParseDownloaded(text) is not { } list)
        {
            log.LogWarning("Public Suffix List download refused: it does not look like a complete list ({Chars} characters)", text.Length);
            return false;
        }

        PublicSuffixList.Use(list);
        _lastModified = res.Content.Headers.LastModified;
        log.LogInformation("Public Suffix List updated: {Rules} rules", list.RuleCount);
        try
        {
            // Garage does not accept aws-chunked uploads (as in ScannerFeeds).
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = ScannerFeeds.BackupBucket, Key = BackupKey, InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
                ContentType = "text/plain", UseChunkEncoding = false,
            }, ct);
        }
        catch (AmazonS3Exception e)
        {
            log.LogWarning(e, "Could not keep a copy of the Public Suffix List in S3");
        }

        return true;
    }

    public async Task LoadBackupAsync(CancellationToken ct)
    {
        try
        {
            using var obj = await s3.GetObjectAsync(ScannerFeeds.BackupBucket, BackupKey, ct);
            using var reader = new StreamReader(obj.ResponseStream);
            var text = await reader.ReadToEndAsync(ct);
            if (PublicSuffixList.TryParseDownloaded(text) is { } list)
            {
                PublicSuffixList.Use(list);
                _lastModified = obj.LastModified is { } lm ? new DateTimeOffset(lm.ToUniversalTime(), TimeSpan.Zero) : null;
                log.LogInformation("Public Suffix List loaded from the last download: {Rules} rules", list.RuleCount);
            }
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // No download yet: the embedded list is used.
        }
        catch (Exception e) when (e is AmazonS3Exception or IOException or HttpRequestException)
        {
            log.LogWarning(e, "Could not read the saved Public Suffix List; using the embedded one");
        }
    }
}
