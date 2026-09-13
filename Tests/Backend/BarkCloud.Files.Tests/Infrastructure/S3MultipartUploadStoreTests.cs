using System.Net;

using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Infrastructure;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Files.Tests.Infrastructure;

public class S3MultipartUploadStoreTests
{
    [Fact]
    public async Task InitiateAsync_DoesNotSendNonAsciiMetadata()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), default))
            .ReturnsAsync(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
        var sut = new S3MultipartUploadStore(Registry(client));

        await sut.InitiateAsync("profile", "file", "image/jpeg", "Фото с обложкой.jpg", default);

        client.Verify(x => x.InitiateMultipartUploadAsync(
            It.Is<InitiateMultipartUploadRequest>(r => r.Metadata.Keys.All(key =>
                (r.Metadata[key] ?? string.Empty).All(static character => character <= 0x7F))),
            default));
    }

    [Fact]
    public async Task UploadPartAsync_StreamsPartToSelectedProfile()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), default))
            .ReturnsAsync(new UploadPartResponse { ETag = "etag-2" });
        var sut = new S3MultipartUploadStore(Registry(client));
        await using var body = new MemoryStream(new byte[12]);

        var result = await sut.UploadPartAsync("profile", "file", "upload", 2, body, 12, default);

        result.Should().Be(new MultipartUploadPart(2, 12, "etag-2"));
        client.Verify(x => x.UploadPartAsync(
            It.Is<UploadPartRequest>(r => r.BucketName == "bucket"
                                          && r.Key == "file"
                                          && r.UploadId == "upload"
                                          && r.PartNumber == 2
                                          && r.PartSize == 12
                                          && ReferenceEquals(r.InputStream, body)),
            default));
    }

    [Fact]
    public async Task UploadPartAsync_ForR2_DisablesStreamingPayloadSigning()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), default))
            .ReturnsAsync(new UploadPartResponse { ETag = "etag-r2" });
        var sut = new S3MultipartUploadStore(Registry(client, isR2: true));
        await using var body = new MemoryStream(new byte[12]);

        await sut.UploadPartAsync("profile", "file", "upload", 1, body, 12, default);

        client.Verify(x => x.UploadPartAsync(
            It.Is<UploadPartRequest>(r => r.DisablePayloadSigning == true
                                          && r.DisableDefaultChecksumValidation == true),
            default));
    }

    [Fact]
    public async Task ListPartsAsync_ReturnsAllPagesInPartOrder()
    {
        var client = new Mock<IAmazonS3>();
        client.SetupSequence(x => x.ListPartsAsync(It.IsAny<ListPartsRequest>(), default))
            .ReturnsAsync(new ListPartsResponse
            {
                IsTruncated = true,
                NextPartNumberMarker = 1,
                Parts = [new PartDetail { PartNumber = 1, Size = 10, ETag = "one" }]
            })
            .ReturnsAsync(new ListPartsResponse
            {
                IsTruncated = false,
                Parts = [new PartDetail { PartNumber = 2, Size = 5, ETag = "two" }]
            });
        var sut = new S3MultipartUploadStore(Registry(client));

        var result = await sut.ListPartsAsync("profile", "file", "upload", default);

        result.Should().Equal(
            new MultipartUploadPart(1, 10, "one"),
            new MultipartUploadPart(2, 5, "two"));
    }

    [Fact]
    public async Task ListPartsAsync_WhenProviderReturnsNoParts_ReturnsEmpty()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(x => x.ListPartsAsync(It.IsAny<ListPartsRequest>(), default))
            .ReturnsAsync(new ListPartsResponse { IsTruncated = false, Parts = null! });
        var sut = new S3MultipartUploadStore(Registry(client));

        var result = await sut.ListPartsAsync("profile", "file", "upload", default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task HeadAsync_WhenObjectDoesNotExist_ReturnsNull()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(x => x.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), default))
            .ThrowsAsync(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound });
        var sut = new S3MultipartUploadStore(Registry(client));

        var result = await sut.HeadAsync("profile", "file", default);

        result.Should().BeNull();
    }

    private static S3BucketRegistry Registry(Mock<IAmazonS3> client, bool isR2 = false)
    {
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(x => x.GetProfile("profile")).Returns(new StorageProfileOptions
        {
            ProfileId = "profile",
            Role = "universal",
            ServiceUrl = "http://localhost:9000",
            AccessKey = "key",
            SecretKey = "secret",
            BucketName = "bucket",
            IsR2 = isR2
        });
        registry.Setup(x => x.GetClientForProfile("profile")).Returns(client.Object);
        return registry.Object;
    }
}
