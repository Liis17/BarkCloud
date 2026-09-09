using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Configurations;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

using System.Net.Sockets;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class S3BucketInitializerTests
{
    [Fact]
    public void IsTransientStartupException_RecognisesConnectionRefused()
    {
        var exception = new HttpRequestException(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));

        S3BucketInitializer.IsTransientStartupException(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransientStartupException_DoesNotRetryPermanentErrors()
    {
        S3BucketInitializer.IsTransientStartupException(new UnauthorizedAccessException()).Should().BeFalse();
    }

    [Fact]
    public async Task InitializeBuckets_R2ChecksBucketButNeverCreatesIt()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response());
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(value => value.GetAllProfiles()).Returns(new[]
        {
            (new StorageProfileOptions
            {
                ProfileId = "images-v1", Role = "images", ServiceUrl = "https://r2.example",
                AccessKey = "key", SecretKey = "secret", BucketName = "images", IsR2 = true
            }, client.Object)
        });

        await new S3BucketInitializer(registry.Object, NullLogger<S3BucketInitializer>.Instance)
            .InitializeBucketsAsync();

        client.Verify(value => value.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == "images"),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(value => value.PutBucketAsync(
            It.IsAny<PutBucketRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitializeBuckets_LocalS3CreatesMissingBucket()
    {
        var notFound = new AmazonS3Exception("missing") { StatusCode = System.Net.HttpStatusCode.NotFound };
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.GetBucketLocationAsync("previews", It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFound);
        client.Setup(value => value.PutBucketAsync(
                It.IsAny<PutBucketRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutBucketResponse());
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(value => value.GetAllProfiles()).Returns(new[]
        {
            (new StorageProfileOptions
            {
                ProfileId = "previews-v1", Role = "previews", ServiceUrl = "http://minio:9000",
                AccessKey = "key", SecretKey = "secret", BucketName = "previews", IsR2 = false, IsActive = true
            }, client.Object)
        });

        await new S3BucketInitializer(registry.Object, NullLogger<S3BucketInitializer>.Instance)
            .InitializeBucketsAsync();

        client.Verify(value => value.PutBucketAsync(
            It.Is<PutBucketRequest>(request => request.BucketName == "previews"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitializeBuckets_InactiveLocalProfileNeverCreatesMissingBucket()
    {
        var notFound = new AmazonS3Exception("missing") { StatusCode = System.Net.HttpStatusCode.NotFound };
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.GetBucketLocationAsync("old-images", It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFound);
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(value => value.GetAllProfiles()).Returns(new[]
        {
            (new StorageProfileOptions
            {
                ProfileId = "images-v1", Role = "images", ServiceUrl = "http://minio:9000",
                AccessKey = "key", SecretKey = "secret", BucketName = "old-images", IsActive = false
            }, client.Object)
        });

        var act = () => new S3BucketInitializer(registry.Object, NullLogger<S3BucketInitializer>.Instance)
            .InitializeBucketsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Verify(value => value.PutBucketAsync(
            It.IsAny<PutBucketRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitializeBuckets_FailingHistoricalProfileDoesNotSkipActiveProfiles()
    {
        var historicalClient = new Mock<IAmazonS3>();
        historicalClient.Setup(value => value.GetBucketLocationAsync("old-images", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("old endpoint is unavailable"));

        var notFound = new AmazonS3Exception("missing") { StatusCode = System.Net.HttpStatusCode.NotFound };
        var activeClient = new Mock<IAmazonS3>();
        activeClient.Setup(value => value.GetBucketLocationAsync("images", It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFound);
        activeClient.Setup(value => value.PutBucketAsync(
                It.IsAny<PutBucketRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutBucketResponse());

        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(value => value.GetAllProfiles()).Returns(new[]
        {
            (new StorageProfileOptions
            {
                ProfileId = "images-v1", Role = "images", ServiceUrl = "http://old-minio:9000",
                AccessKey = "key", SecretKey = "secret", BucketName = "old-images", IsActive = false
            }, historicalClient.Object),
            (new StorageProfileOptions
            {
                ProfileId = "images-v2", Role = "images", ServiceUrl = "http://minio:9000",
                AccessKey = "key", SecretKey = "secret", BucketName = "images", IsActive = true
            }, activeClient.Object)
        });

        var act = () => new S3BucketInitializer(registry.Object, NullLogger<S3BucketInitializer>.Instance)
            .InitializeBucketsAsync();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        activeClient.Verify(value => value.PutBucketAsync(
            It.Is<PutBucketRequest>(request => request.BucketName == "images"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
