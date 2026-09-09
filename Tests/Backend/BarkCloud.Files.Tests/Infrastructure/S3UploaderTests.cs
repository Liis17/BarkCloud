using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Configurations;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Files.Tests.Infrastructure;

public class S3UploaderTests
{
    [Fact]
    public void CalculatePartSize_Uses64MiBForOrdinaryMultipartObject()
    {
        S3Uploader.CalculatePartSize(5L * 1024 * 1024 * 1024)
            .Should().Be(S3Uploader.BasePartSize);
    }

    [Fact]
    public void CalculatePartSize_IncreasesPartSizeToStayWithinTenThousandParts()
    {
        var huge = S3Uploader.BasePartSize * 10_001;

        var partSize = S3Uploader.CalculatePartSize(huge);

        ((huge + partSize - 1) / partSize).Should().BeLessThanOrEqualTo(10_000);
    }

    [Fact]
    public async Task UploadAsync_WhenMultipartPartFails_AbortsUpload()
    {
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.InitiateMultipartUploadAsync(
                It.IsAny<InitiateMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
        client.Setup(value => value.UploadPartAsync(
                It.IsAny<UploadPartRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("failed"));
        client.Setup(value => value.AbortMultipartUploadAsync(
                It.IsAny<AbortMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbortMultipartUploadResponse());
        var registry = new Mock<S3BucketRegistry>(new ConfigurationBuilder().Build()) { CallBase = false };
        registry.Setup(value => value.GetProfile("videos-v1")).Returns(new StorageProfileOptions
        {
            ProfileId = "videos-v1", Role = "videos", ServiceUrl = "https://s3.example",
            AccessKey = "key", SecretKey = "secret", BucketName = "videos"
        });
        registry.Setup(value => value.GetClientForProfile("videos-v1")).Returns(client.Object);
        var uploader = new S3Uploader(registry.Object);
        await using var stream = new LengthOnlyStream(S3Uploader.MultipartThreshold);

        var act = () => uploader.UploadAsync("videos-v1", "file", stream, "video/mp4");

        await act.Should().ThrowAsync<IOException>();
        client.Verify(value => value.AbortMultipartUploadAsync(
            It.Is<AbortMultipartUploadRequest>(request => request.UploadId == "upload-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => Position;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
