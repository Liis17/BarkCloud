using System.Net;
using System.Net.Http.Headers;

using Amazon.Runtime;
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
                                          && r.DisableDefaultChecksumValidation == true
                                          && r.UseChunkEncoding == false),
            default));
    }

    [Fact]
    public async Task UploadPartAsync_ForR2_StreamsNonSeekableBody()
    {
        var handler = new CapturingHttpMessageHandler();
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("key", "secret"),
            new AmazonS3Config
            {
                ServiceURL = "https://r2.test",
                ForcePathStyle = true,
                AuthenticationRegion = "auto",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                HttpClientFactory = new SingleHttpClientFactory(handler)
            });
        var sut = new S3MultipartUploadStore(Registry(client, isR2: true));
        var bytes = Enumerable.Range(0, 12).Select(static value => (byte)value).ToArray();
        await using var body = new NonSeekableReadStream(bytes);

        var result = await sut.UploadPartAsync("profile", "file", "upload", 1, body, bytes.Length, default);

        result.PartNumber.Should().Be(1);
        result.Size.Should().Be(bytes.Length);
        handler.Body.Should().Equal(bytes);
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
        return Registry(client.Object, isR2);
    }

    private static S3BucketRegistry Registry(IAmazonS3 client, bool isR2 = false)
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
        registry.Setup(x => x.GetClientForProfile("profile")).Returns(client);
        return registry.Object;
    }

    private sealed class SingleHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"etag\"");
            return response;
        }
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
