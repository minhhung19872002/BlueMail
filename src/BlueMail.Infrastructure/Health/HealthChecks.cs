using Amazon.S3;
using BlueMail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BlueMail.Infrastructure.Health;

public sealed class PostgresHealthCheck(BlueMailDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

public sealed class ValkeyHealthCheck(IConnectionMultiplexer valkey) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await valkey.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Valkey unreachable", ex);
        }
    }
}

public sealed class S3HealthCheck(IAmazonS3 s3) : IHealthCheck
{
    public const string ProbeBucket = "bm-messages";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = ProbeBucket, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is AmazonS3Exception or HttpRequestException or Amazon.Runtime.AmazonClientException)
        {
            return HealthCheckResult.Unhealthy("S3 unreachable", ex);
        }
    }
}
