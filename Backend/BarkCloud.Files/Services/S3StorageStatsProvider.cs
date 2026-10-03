using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;

namespace BarkCloud.Files.Services;

public sealed class S3StorageStatsProvider : IS3StorageStatsProvider
{
    private readonly S3BucketRegistry _registry;
    private readonly StorageStatsCache<S3StorageStats> _cache;

    public S3StorageStatsProvider(
        S3BucketRegistry registry,
        TimeProvider timeProvider,
        ILogger<S3StorageStatsProvider> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _registry = registry;
        _cache = new(ReadStatsAsync, timeProvider, logger, lifetime?.ApplicationStopping ?? CancellationToken.None);
    }

    public Task<S3StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
        => GetAsync(false, cancellationToken);

    public Task<S3StorageStats> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => GetAsync(true, cancellationToken);

    private async Task<S3StorageStats> GetAsync(bool nonBlocking, CancellationToken cancellationToken)
    {
        var snapshot = await _cache.GetAsync(nonBlocking, cancellationToken);
        var value = snapshot.Value ?? S3StorageStats.Unavailable;
        return value with
        {
            State = snapshot.State == "ready" && !value.IsAvailable ? "not_configured" : snapshot.State,
            UpdatedAt = snapshot.UpdatedAt
        };
    }

    private async Task<S3StorageStats> ReadStatsAsync(CancellationToken cancellationToken)
    {
        var buckets = _registry.GetAllProfiles()
            .Where(item => item.Profile.IsActive && !item.Profile.IsLegacy)
            .GroupBy(item => new PhysicalBucket(NormalizeEndpoint(item.Profile.ServiceUrl), item.Profile.BucketName))
            .Select(group => new Bucket(
                group.Key.BucketName,
                group.Min(item => item.Profile.QuotaBytes),
                group.Select(item => item.Client).Distinct().ToArray()))
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
        List<Exception>? failures = null;
        foreach (var client in bucket.Clients)
        {
            try
            {
                return await GetBucketSizeAsync(bucket.Name, client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        throw new AggregateException(
            $"Не удалось получить размер S3-бакета '{bucket.Name}' через доступные профили.",
            failures!);
    }

    private static async Task<long> GetBucketSizeAsync(
        string bucketName,
        IAmazonS3 client,
        CancellationToken cancellationToken)
    {
        long usedBytes = 0;
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketName,
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
                throw new InvalidOperationException($"S3 не вернул continuation token для следующей страницы бакета '{bucketName}'.");
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

    private sealed record Bucket(string Name, long QuotaBytes, IReadOnlyList<IAmazonS3> Clients);
}
