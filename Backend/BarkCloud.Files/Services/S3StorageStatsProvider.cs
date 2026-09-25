using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;

namespace BarkCloud.Files.Services;

public sealed class S3StorageStatsProvider : IS3StorageStatsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly S3BucketRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<S3StorageStatsProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private S3StorageStats? _lastSuccessful;
    private DateTimeOffset? _lastAttemptAt;

    public S3StorageStatsProvider(
        S3BucketRegistry registry,
        TimeProvider timeProvider,
        ILogger<S3StorageStatsProvider> logger)
    {
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<S3StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastAttemptAt is { } cachedAttempt && now - cachedAttempt < CacheDuration)
                return _lastSuccessful ?? S3StorageStats.Unavailable;

            try
            {
                var stats = await ReadStatsAsync(cancellationToken);
                _lastSuccessful = stats;
                _lastAttemptAt = _timeProvider.GetUtcNow();
                return stats;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _lastAttemptAt = _timeProvider.GetUtcNow();
                _logger.LogWarning(exception, "Не удалось получить суммарный размер S3-бакетов; используем последнюю успешную статистику, если она есть.");
                return _lastSuccessful ?? S3StorageStats.Unavailable;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<S3StorageStats> ReadStatsAsync(CancellationToken cancellationToken)
    {
        var buckets = _registry.GetAllProfiles()
            .Where(item => item.Profile.IsActive && !item.Profile.IsLegacy)
            .GroupBy(item => new PhysicalBucket(NormalizeEndpoint(item.Profile.ServiceUrl), item.Profile.BucketName))
            .Select(group => new Bucket(
                group.Key.BucketName,
                group.Min(item => item.Profile.QuotaBytes),
                group.First().Client))
            .ToArray();

        if (buckets.Length == 0)
            return S3StorageStats.Unavailable;

        var sizes = await Task.WhenAll(buckets.Select(bucket => GetBucketSizeAsync(bucket, cancellationToken)));
        long usedBytes = 0;
        long quotaBytes = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            usedBytes = checked(usedBytes + sizes[index]);
            quotaBytes = checked(quotaBytes + buckets[index].QuotaBytes);
        }

        var hasFiniteQuota = buckets.Length > 0 && buckets.All(bucket => bucket.QuotaBytes > 0);
        return new S3StorageStats(usedBytes, quotaBytes, hasFiniteQuota, true);
    }

    private static async Task<long> GetBucketSizeAsync(Bucket bucket, CancellationToken cancellationToken)
    {
        long usedBytes = 0;
        string? continuationToken = null;
        do
        {
            var response = await bucket.Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket.Name,
                ContinuationToken = continuationToken
            }, cancellationToken);

            if (response.S3Objects is { } objects)
            {
                foreach (var item in objects)
                    usedBytes = checked(usedBytes + item.Size.GetValueOrDefault());
            }

            if (!response.IsTruncated.GetValueOrDefault())
                break;
            if (string.IsNullOrEmpty(response.NextContinuationToken))
                throw new InvalidOperationException($"S3 не вернул continuation token для следующей страницы бакета '{bucket.Name}'.");
            continuationToken = response.NextContinuationToken;
        } while (true);

        return usedBytes;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private readonly record struct PhysicalBucket(string Endpoint, string BucketName);

    private sealed record Bucket(string Name, long QuotaBytes, IAmazonS3 Client);
}
