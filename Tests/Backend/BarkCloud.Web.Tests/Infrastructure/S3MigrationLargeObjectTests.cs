using System.Net;
using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class MigrationLargeObjectFactAttribute : FactAttribute
{
    public MigrationLargeObjectFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_LARGE_TEST") != "1")
            Skip = "Полный потоковый прогон 22 ГиБ: BARKCLOUD_MIGRATION_LARGE_TEST=1.";
    }
}

public sealed class S3MigrationLargeObjectTests
{
    [MigrationLargeObjectFact]
    public async Task TwentyTwoGiB_UsesLongRangesAndOnePartBufferAndVerifiesAllUploadedContent()
    {
        const long size = 22L * 1024 * 1024 * 1024 + 3;
        var source = new Mock<IAmazonS3>(MockBehavior.Strict); var target = new Mock<IAmazonS3>(MockBehavior.Strict);
        var metadata = new GetObjectMetadataResponse { ContentLength = size, ETag = "source-etag" };
        metadata.Headers.ContentType = "video/mp4";
        source.Setup(x => x.GetObjectMetadataAsync("source", "large.mp4", It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        var destinationMetadata = new GetObjectMetadataResponse { ContentLength = size, ETag = "multipart-etag" };
        destinationMetadata.Headers.ContentType = "video/mp4";
        target.Setup(x => x.GetObjectMetadataAsync("target", "large.mp4", It.IsAny<CancellationToken>())).ReturnsAsync(destinationMetadata);
        var sourceRanges = new List<long>(); var destinationRanges = new List<long>();
        GetObjectResponse Range(GetObjectRequest request, string etag, List<long> ranges)
        {
            request.EtagToMatch.Should().Be(etag); request.ByteRange.Should().NotBeNull();
            var start = request.ByteRange.Start; var length = request.ByteRange.End - start + 1;
            start.Should().Be(ranges.Count * S3MigrationCopier.PartSize); length.Should().BeInRange(1, S3MigrationCopier.PartSize);
            ranges.Add(start);
            return new() { ETag = etag, ContentLength = length, ContentRange = $"bytes {start}-{request.ByteRange.End}/{size}",
                HttpStatusCode = HttpStatusCode.PartialContent, ResponseStream = new PatternStream(start, length) };
        }
        source.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetObjectRequest request, CancellationToken _) => Range(request, "source-etag", sourceRanges));
        target.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetObjectRequest request, CancellationToken _) => Range(request, "multipart-etag", destinationRanges));
        target.Setup(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InitiateMultipartUploadResponse { UploadId = "large-upload" });
        using var uploadedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long uploaded = 0; var partCount = 0; var buffer = new byte[65536];
        target.Setup(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (UploadPartRequest request, CancellationToken ct) =>
            {
                request.PartNumber.Should().Be(++partCount); request.InputStream.Length.Should().Be(request.PartSize);
                request.InputStream.Length.Should().BeLessThanOrEqualTo(S3MigrationCopier.PartSize);
                long read = 0; int count;
                while ((count = await request.InputStream.ReadAsync(buffer, ct)) != 0)
                { uploadedHash.AppendData(buffer, 0, count); read += count; }
                read.Should().Be(request.PartSize); uploaded += read;
                return new UploadPartResponse { ETag = "part-" + request.PartNumber };
            });
        target.Setup(x => x.CompleteMultipartUploadAsync(It.IsAny<CompleteMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CompleteMultipartUploadRequest request, CancellationToken _) =>
            { request.PartETags.Should().HaveCount(partCount); uploaded.Should().Be(size); return new CompleteMultipartUploadResponse(); });
        var directory = Path.Combine(Path.GetTempPath(), "bc-22gib-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var copier = new S3MigrationCopier(new MigrationS3ClientFactory());
            var copied = await copier.CopyAsync(source.Object, new("https://source.example", "test", "test", "source"),
                target.Object, new("https://target.example", "test", "test", "target"), new("large.mp4", size, metadata.ETag, null),
                directory, _ => {}, deadline.Token);
            copied.Size.Should().Be(size); copied.Sha256.Should().Be(Convert.ToHexString(uploadedHash.GetHashAndReset()));
            partCount.Should().Be(353); sourceRanges.Should().HaveCount(partCount); destinationRanges.Should().Equal(sourceRanges);
            sourceRanges.Last().Should().Be(22L * 1024 * 1024 * 1024);
            Directory.GetFiles(directory).Should().BeEmpty();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // Generate every byte of the logical object without storing a 22 GiB fixture in RAM or on disk.
    private sealed class PatternStream(long offset, long length) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offsetInBuffer, int count) => Read(buffer.AsSpan(offsetInBuffer, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _read); var written = 0;
            while (written < count)
            {
                var position = offset + _read + written;
                var block = (int)Math.Min(count - written, 65536 - position % 65536);
                buffer.Slice(written, block).Fill((byte)(position / 65536 % 251)); written += block;
            }
            _read += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
