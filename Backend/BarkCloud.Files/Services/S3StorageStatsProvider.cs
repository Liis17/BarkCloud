using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;
using BarkCloud.GrpcServer;

using Microsoft.Extensions.Configuration;

using System.Globalization;

namespace BarkCloud.Files.Services;

public sealed class S3StorageStatsProvider : IS3StorageStatsProvider
{
    private readonly S3BucketRegistry _registry;
    private readonly IReadOnlyList<StorageQuotaGroupOptions> _groups;
    private readonly StorageStatsCache<S3StorageStats> _cache;

    public S3StorageStatsProvider(
        S3BucketRegistry registry,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<S3StorageStatsProvider> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _registry = registry;
        _groups = configuration.GetSection("StorageQuotaGroups").GetChildren()
            .Select(section => new StorageQuotaGroupOptions
            {
                Id = section.Key,
                Name = section["Name"] ?? string.Empty,
                QuotaBytes = long.Parse(section["QuotaBytes"] ?? "0", CultureInfo.InvariantCulture),
                Buckets = section.GetSection("Buckets").GetChildren()
                    .Select(bucket => new StorageQuotaGroupBucketOptions
                    {
                        ServiceUrl = bucket["ServiceUrl"] ?? string.Empty,
                        BucketName = bucket["BucketName"] ?? string.Empty
                    })
                    .ToList()
            })
            .ToArray();
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
            .GroupBy(item => new PhysicalBucket(
                S3Endpoint.Normalize(item.Profile.ServiceUrl, item.Profile.IsR2), item.Profile.BucketName),
                PhysicalBucketComparer.Instance)
            .ToDictionary(group => group.Key, group => new Bucket(
                group.Key.BucketName,
                group.Any(item => item.Profile.IsActive && !item.Profile.IsLegacy),
                group.Select(item => item.Client).Distinct().ToArray()),
                PhysicalBucketComparer.Instance);

        var groups = _groups.Select(group => new ConfiguredGroup(
                group.Id,
                group.Name,
                group.QuotaBytes,
                group.Buckets.Select(bucket => new PhysicalBucket(
                    S3Endpoint.Normalize(bucket.ServiceUrl), bucket.BucketName))
                    .Distinct(PhysicalBucketComparer.Instance)
                    .ToArray()))
            .OrderBy(group => group.Name, StringComparer.Ordinal)
            .ToArray();
        var groupedBuckets = groups.SelectMany(group => group.Buckets)
            .ToHashSet(PhysicalBucketComparer.Instance);
        var ungroupedBuckets = buckets.Where(item => item.Value.IsActive && !groupedBuckets.Contains(item.Key))
            .Select(item => item.Key)
            .ToArray();
        var bucketsToMeasure = groups.SelectMany(group => group.Buckets)
            .Where(buckets.ContainsKey)
            .Concat(ungroupedBuckets)
            .Distinct(PhysicalBucketComparer.Instance)
            .ToArray();

        if (groups.Length == 0 && bucketsToMeasure.Length == 0)
            return S3StorageStats.Unavailable;

        var measured = await Task.WhenAll(bucketsToMeasure.Select(async key =>
            (Key: key, UsedBytes: await GetBucketSizeAsync(buckets[key], cancellationToken))));
        var usedByBucket = measured.ToDictionary(item => item.Key, item => item.UsedBytes, PhysicalBucketComparer.Instance);

        var results = groups.Select(group => new S3QuotaGroupStats(
                group.Id,
                group.Name,
                SumUsedBytes(group.Buckets.Where(usedByBucket.ContainsKey), usedByBucket),
                group.QuotaBytes,
                group.Buckets.Count(key => !buckets.ContainsKey(key)),
                false))
            .ToList();
        if (ungroupedBuckets.Length > 0)
        {
            results.Add(new S3QuotaGroupStats(
                string.Empty,
                "Без группы",
                SumUsedBytes(ungroupedBuckets, usedByBucket),
                0,
                0,
                true));
        }

        return new S3StorageStats(results, true);
    }

    private static long SumUsedBytes(
        IEnumerable<PhysicalBucket> buckets,
        IReadOnlyDictionary<PhysicalBucket, long> usedByBucket)
    {
        long total = 0;
        foreach (var bucket in buckets)
            total = checked(total + usedByBucket[bucket]);
        return total;
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

    private readonly record struct PhysicalBucket(string Endpoint, string BucketName);

    private sealed record ConfiguredGroup(string Id, string Name, long QuotaBytes, IReadOnlyList<PhysicalBucket> Buckets);

    private sealed record Bucket(string Name, bool IsActive, IReadOnlyList<IAmazonS3> Clients);

    private sealed class PhysicalBucketComparer : IEqualityComparer<PhysicalBucket>
    {
        public static PhysicalBucketComparer Instance { get; } = new();

        public bool Equals(PhysicalBucket left, PhysicalBucket right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Endpoint, right.Endpoint)
            && StringComparer.Ordinal.Equals(left.BucketName, right.BucketName);

        public int GetHashCode(PhysicalBucket bucket) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(bucket.Endpoint),
            StringComparer.Ordinal.GetHashCode(bucket.BucketName));
    }
}
