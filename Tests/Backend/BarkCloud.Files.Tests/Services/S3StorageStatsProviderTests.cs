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
    public async Task GetStatsAsync_SumsEveryPageAndCountsOnlyUniqueActiveNonLegacyBuckets()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "shared" && request.ContinuationToken == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(10, "preview/photo.jpg", 20, truncated: true, next: "page-2"));
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "shared" && request.ContinuationToken == "page-2"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(30, "original/photo.heic"));
        client.Setup(s3 => s3.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == "other"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(40, "document.pdf"));

        var profiles = new[]
        {
            (Profile("images-v1", "shared", active: true), client.Object),
            (Profile("previews-v1", "shared", active: true), client.Object),
            (Profile("other-v1", "other", active: true), client.Object),
            (Profile("old-v1", "legacy", active: true, legacy: true), client.Object),
            (Profile("inactive-v1", "inactive", active: false), client.Object)
        };
        var sut = CreateProvider(profiles);

        var stats = await sut.GetStatsAsync();

        stats.IsAvailable.Should().BeTrue();
        stats.UsedBytes.Should().Be(100);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "shared"), It.IsAny<CancellationToken>()), Times.Exactly(2));
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "other"), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(s3 => s3.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "legacy" || request.BucketName == "inactive"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStatsAsync_SumsQuotaOncePerPhysicalBucketAndMarksMixedQuotaUnlimited()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [] });
        var profiles = new[]
        {
            (Profile("images-v1", "shared", active: true, quota: 12), client.Object),
            (Profile("previews-v1", "shared", active: true, quota: 12), client.Object),
            (Profile("videos-v1", "videos", active: true, quota: 30), client.Object)
        };

        var finite = await CreateProvider(profiles).GetStatsAsync();

        finite.HasFiniteQuota.Should().BeTrue();
        finite.QuotaBytes.Should().Be(42);

        var mixed = await CreateProvider(new[]
        {
            (Profile("images-v1", "shared", active: true, quota: 12), client.Object),
            (Profile("previews-v1", "shared", active: true, quota: 12), client.Object),
            (Profile("videos-v1", "videos", active: true), client.Object)
        }).GetStatsAsync();

        mixed.HasFiniteQuota.Should().BeFalse();
        mixed.QuotaBytes.Should().Be(12);
    }

    [Fact]
    public async Task GetStatsAsync_UsesLastSuccessfulSnapshotAfterRefreshFailure()
    {
        var client = new Mock<IAmazonS3>();
        client.SetupSequence(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(17, "object"))
            .ThrowsAsync(new AmazonS3Exception("temporary failure"));
        var time = new MutableTimeProvider();
        var sut = CreateProvider([(Profile("universal-v1", "shared", active: true), client.Object)], time);

        var initial = await sut.GetStatsAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        var refreshed = await sut.GetStatsAsync();
        var cached = await sut.GetStatsAsync();

        initial.IsAvailable.Should().BeTrue();
        refreshed.IsAvailable.Should().BeTrue();
        refreshed.UsedBytes.Should().Be(17);
        cached.UsedBytes.Should().Be(17);
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
        stats.UsedBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_ReportsUnavailableWhenThereAreNoActiveBuckets()
    {
        var client = new Mock<IAmazonS3>();
        var profiles = new[]
        {
            (Profile("inactive-v1", "inactive", active: false), client.Object),
            (Profile("legacy-v1", "legacy", active: true, legacy: true), client.Object)
        };

        var stats = await CreateProvider(profiles).GetStatsAsync();

        stats.IsAvailable.Should().BeFalse();
        client.Verify(s3 => s3.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static S3StorageStatsProvider CreateProvider(
        IEnumerable<(StorageProfileOptions Profile, IAmazonS3 Client)> profiles,
        TimeProvider? timeProvider = null)
    {
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build());
        registry.Setup(value => value.GetAllProfiles()).Returns(profiles);
        return new S3StorageStatsProvider(
            registry.Object,
            timeProvider ?? TimeProvider.System,
            NullLogger<S3StorageStatsProvider>.Instance);
    }

    private static StorageProfileOptions Profile(
        string id,
        string bucket,
        bool active,
        bool legacy = false,
        long quota = 0) => new()
    {
        ProfileId = id,
        Role = id.Split('-')[0],
        ServiceUrl = "https://s3.example",
        AccessKey = "access",
        SecretKey = "secret",
        BucketName = bucket,
        IsActive = active,
        IsLegacy = legacy,
        QuotaBytes = quota
    };

    private static ListObjectsV2Response Page(
        long firstSize,
        string firstKey,
        long? secondSize = null,
        string? secondKey = null,
        bool truncated = false,
        string? next = null)
    {
        var response = new ListObjectsV2Response
        {
            IsTruncated = truncated,
            NextContinuationToken = next,
            S3Objects = []
        };
        response.S3Objects.Add(new S3Object { Key = firstKey, Size = firstSize });
        if (secondSize is not null)
            response.S3Objects.Add(new S3Object { Key = secondKey, Size = secondSize.Value });
        return response;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
