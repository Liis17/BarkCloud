using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public sealed class S3StorageStatsProviderTests
{
    [Fact]
    public async Task GetStatsAsync_SumsGroupsOncePerPhysicalBucketAndAddsUngroupedActiveBuckets()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "shared"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(10, "photo.jpg"));
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "inactive-grouped"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(20, "archive.zip"));
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "other"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(40, "document.pdf"));

        var profiles = new[]
        {
            (Profile("images-v1", "shared", active: true), client.Object),
            (Profile("previews-v1", "shared", active: true), client.Object),
            (Profile("archive-v1", "inactive-grouped", active: false), client.Object),
            (Profile("other-v1", "other", active: true), client.Object),
            (Profile("old-v1", "legacy", active: true, legacy: true), client.Object),
            (Profile("inactive-v1", "inactive", active: false), client.Object)
        };
        var groups = new[]
        {
            Group("group-1", "Основная", 100, Ref("https://s3.example", "shared"), Ref("https://s3.example", "inactive-grouped"))
        };

        var stats = await CreateProvider(profiles, groups).GetStatsAsync();

        stats.IsAvailable.Should().BeTrue();
        stats.Groups.Should().HaveCount(2);
        stats.Groups[0].Should().Be(new S3QuotaGroupStats("group-1", "Основная", 30, 100, 0, false));
        stats.Groups[1].Should().Be(new S3QuotaGroupStats("", "Без группы", 40, 0, 0, true));
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "shared"), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "inactive-grouped"), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "other"), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "legacy" || request.BucketName == "inactive"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStatsAsync_TriesOtherCredentialsForTheSamePhysicalBucket()
    {
        var deniedClient = new Mock<IAmazonS3>();
        deniedClient.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("access denied"));
        var readableClient = new Mock<IAmazonS3>();
        readableClient.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(23, "object"));
        var profiles = new[]
        {
            (Profile("images-v1", "shared", active: true), deniedClient.Object),
            (Profile("previews-v1", "shared", active: true), readableClient.Object)
        };

        var stats = await CreateProvider(profiles).GetStatsAsync();

        stats.IsAvailable.Should().BeTrue();
        stats.Groups.Should().ContainSingle(group => group.IsUngrouped && group.UsedBytes == 23);
        deniedClient.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Once);
        readableClient.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStatsAsync_SumsEveryPageForAGroupBucket()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "photos" && request.ContinuationToken == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                IsTruncated = true,
                NextContinuationToken = "page-2",
                S3Objects = [new S3Object { Key = "one", Size = 10 }, new S3Object { Key = "two", Size = 20 }]
            });
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "photos" && request.ContinuationToken == "page-2"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(30, "three"));
        var profiles = new[] { (Profile("images-v1", "photos", active: true), client.Object) };

        var stats = await CreateProvider(profiles,
            [Group("g1", "Main", 100, Ref("https://s3.example", "photos"))]).GetStatsAsync();

        stats.Groups.Should().ContainSingle(group => group.UsedBytes == 60);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.ContinuationToken == null), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.ContinuationToken == "page-2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStatsAsync_DoesNotRequestUnavailableInactiveBucketOutsideGroups()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "measured"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(5, "object"));
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "inactive"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("unavailable"));

        var profiles = new[]
        {
            (Profile("images-v1", "measured", active: true), client.Object),
            (Profile("documents-v1", "inactive", active: false), client.Object)
        };
        var stats = await CreateProvider(profiles, [Group("g1", "Group", 0, Ref("https://s3.example", "measured"))])
            .GetStatsAsync();

        stats.IsAvailable.Should().BeTrue();
        stats.Groups.Should().ContainSingle(group => group.UsedBytes == 5);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "inactive"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStatsAsync_ReportsUnmeasuredMembersAndKeepsMeasuredUsage()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(23, "object"));
        var profiles = new[] { (Profile("images-v1", "measured", active: true), client.Object) };
        var group = Group("g1", "Group", 50,
            Ref("https://s3.example", "measured"), Ref("https://source.example", "no-profile"));

        var stats = await CreateProvider(profiles, [group]).GetStatsAsync();

        stats.Groups.Should().ContainSingle(groupStats => groupStats.Name == "Group"
            && groupStats.UsedBytes == 23 && groupStats.QuotaBytes == 50 && groupStats.UnmeasuredBuckets == 1);
        client.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStatsAsync_PreservesZeroQuotaAsUnlimited()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [] });
        var profiles = new[] { (Profile("images-v1", "shared", active: false), client.Object) };

        var stats = await CreateProvider(profiles,
            [Group("g1", "Unlimited", 0, Ref("https://s3.example", "shared"))]).GetStatsAsync();

        stats.Groups.Should().ContainSingle(group => group.QuotaBytes == 0 && !group.IsUngrouped);
    }

    [Fact]
    public async Task GetStatsAsync_UsesLastSuccessfulSnapshotAfterRefreshFailure()
    {
        var client = new Mock<IAmazonS3>();
        client.SetupSequence(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(17, "object"))
            .ThrowsAsync(new AmazonS3Exception("temporary failure"));
        var time = new MutableTimeProvider();
        var sut = CreateProvider([(Profile("universal-v1", "shared", active: true), client.Object)], timeProvider: time);

        var initial = await sut.GetStatsAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        var refreshed = await sut.GetStatsAsync();
        var cached = await sut.GetStatsAsync();

        initial.IsAvailable.Should().BeTrue();
        refreshed.IsAvailable.Should().BeTrue();
        refreshed.Groups.Should().ContainSingle(group => group.UsedBytes == 17 && group.IsUngrouped);
        cached.Groups.Should().ContainSingle(group => group.UsedBytes == 17 && group.IsUngrouped);
        client.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetStatsAsync_ReportsUnavailableUntilFirstSuccessfulListing()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("unreachable"));

        var stats = await CreateProvider([(Profile("universal-v1", "shared", active: true), client.Object)]).GetStatsAsync();

        stats.IsAvailable.Should().BeFalse();
        stats.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_ReportsNotConfiguredWhenThereAreNoGroupsOrBuckets()
    {
        var client = new Mock<IAmazonS3>();
        var profiles = new[]
        {
            (Profile("inactive-v1", "inactive", active: false), client.Object),
            (Profile("legacy-v1", "legacy", active: true, legacy: true), client.Object)
        };

        var stats = await CreateProvider(profiles).GetStatsAsync();

        stats.IsAvailable.Should().BeFalse();
        stats.State.Should().Be("not_configured");
        client.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static S3StorageStatsProvider CreateProvider(
        IEnumerable<(StorageProfileOptions Profile, IAmazonS3 Client)> profiles,
        IEnumerable<StorageQuotaGroupOptions>? groups = null,
        TimeProvider? timeProvider = null)
    {
        var configurationValues = new Dictionary<string, string?>();
        var configuredGroups = groups?.ToArray() ?? [];
        for (var groupIndex = 0; groupIndex < configuredGroups.Length; groupIndex++)
        {
            var group = configuredGroups[groupIndex];
            var prefix = $"StorageQuotaGroups:{group.Id}";
            configurationValues[$"{prefix}:Name"] = group.Name;
            configurationValues[$"{prefix}:QuotaBytes"] = group.QuotaBytes.ToString();
            for (var bucketIndex = 0; bucketIndex < group.Buckets.Count; bucketIndex++)
            {
                var bucketPrefix = $"{prefix}:Buckets:{bucketIndex}";
                configurationValues[$"{bucketPrefix}:ServiceUrl"] = group.Buckets[bucketIndex].ServiceUrl;
                configurationValues[$"{bucketPrefix}:BucketName"] = group.Buckets[bucketIndex].BucketName;
            }
        }

        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build());
        registry.Setup(value => value.GetAllProfiles()).Returns(profiles);
        return new S3StorageStatsProvider(
            registry.Object,
            new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build(),
            timeProvider ?? TimeProvider.System,
            NullLogger<S3StorageStatsProvider>.Instance);
    }

    private static StorageProfileOptions Profile(
        string id,
        string bucket,
        bool active,
        bool legacy = false,
        string serviceUrl = "https://s3.example",
        bool isR2 = false) => new()
    {
        ProfileId = id,
        Role = id.Split('-')[0],
        ServiceUrl = serviceUrl,
        AccessKey = "access",
        SecretKey = "secret",
        BucketName = bucket,
        IsActive = active,
        IsLegacy = legacy,
        IsR2 = isR2
    };

    private static StorageQuotaGroupOptions Group(
        string id,
        string name,
        long quotaBytes,
        params StorageQuotaGroupBucketOptions[] buckets) => new()
    {
        Id = id,
        Name = name,
        QuotaBytes = quotaBytes,
        Buckets = buckets.ToList()
    };

    private static StorageQuotaGroupBucketOptions Ref(string serviceUrl, string bucketName) => new()
    {
        ServiceUrl = serviceUrl,
        BucketName = bucketName
    };

    private static ListObjectsV2Response Page(long size, string key) => new()
    {
        S3Objects = [new S3Object { Key = key, Size = size }]
    };

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
