using Amazon.S3.Model;

namespace BarkCloud.Files.Infrastructure;

public class S3Uploader
{
    public const long MultipartThreshold = 100L * 1024 * 1024;
    public const long BasePartSize = 64L * 1024 * 1024;
    private const int MaximumParts = 10_000;

    private readonly S3BucketRegistry _registry;

    public S3Uploader(S3BucketRegistry registry)
    {
        _registry = registry;
    }

    public virtual Task<string> UploadAsync(
        string storageProfileId,
        string key,
        Stream data,
        string contentType) =>
        UploadCoreAsync(storageProfileId, key, data, contentType, CancellationToken.None);

    public Task<string> UploadAsync(
        string storageProfileId,
        string key,
        Stream data,
        string contentType,
        CancellationToken cancellationToken) =>
        UploadCoreAsync(storageProfileId, key, data, contentType, cancellationToken);

    private async Task<string> UploadCoreAsync(
        string storageProfileId,
        string key,
        Stream data,
        string contentType,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var client = _registry.GetClientForProfile(storageProfileId);
        var size = data.CanSeek ? data.Length - data.Position : -1;
        if (size >= MultipartThreshold)
            return await MultipartUploadAsync(storageProfileId, key, data, contentType, size, cancellationToken);

        var request = new PutObjectRequest
        {
            BucketName = profile.BucketName,
            Key = key,
            InputStream = data,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            ContentType = contentType,
            DisablePayloadSigning = profile.IsR2,
            DisableDefaultChecksumValidation = profile.IsR2,
            Metadata = { ["original-filename"] = Path.GetFileName(key) }
        };
        var response = await client.PutObjectAsync(request, cancellationToken);
        return response.ETag;
    }

    public virtual Task DeleteAsync(string storageProfileId, string key) =>
        DeleteAsync(storageProfileId, key, CancellationToken.None);

    public async Task DeleteAsync(
        string storageProfileId,
        string key,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var client = _registry.GetClientForProfile(storageProfileId);
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = profile.BucketName,
            Key = key
        }, cancellationToken);
    }

    public virtual Task<Stream> DownloadAsync(string storageProfileId, string key) =>
        DownloadAsync(storageProfileId, key, CancellationToken.None);

    public async Task<Stream> DownloadAsync(
        string storageProfileId,
        string key,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var response = await _registry.GetClientForProfile(storageProfileId).GetObjectAsync(new GetObjectRequest
        {
            BucketName = profile.BucketName,
            Key = key
        }, cancellationToken);
        return new S3ObjectStream(response);
    }

    public virtual Task<Stream> DownloadRangeAsync(string storageProfileId, string key, long start, long end) =>
        DownloadRangeAsync(storageProfileId, key, start, end, CancellationToken.None);

    public async Task<Stream> DownloadRangeAsync(
        string storageProfileId,
        string key,
        long start,
        long end,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var response = await _registry.GetClientForProfile(storageProfileId).GetObjectAsync(new GetObjectRequest
        {
            BucketName = profile.BucketName,
            Key = key,
            ByteRange = new ByteRange(start, end)
        }, cancellationToken);
        return new S3ObjectStream(response);
    }

    public static long CalculatePartSize(long objectSize)
    {
        if (objectSize < 0)
            throw new ArgumentOutOfRangeException(nameof(objectSize));
        var required = (objectSize + MaximumParts - 1) / MaximumParts;
        return Math.Max(BasePartSize, required);
    }

    private async Task<string> MultipartUploadAsync(
        string storageProfileId,
        string key,
        Stream data,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var client = _registry.GetClientForProfile(storageProfileId);
        var initiated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = profile.BucketName,
            Key = key,
            ContentType = contentType,
            Metadata = { ["original-filename"] = Path.GetFileName(key) }
        }, cancellationToken);

        try
        {
            var parts = new List<PartETag>();
            var partSize = CalculatePartSize(size);
            var remaining = size;
            var partNumber = 1;
            while (remaining > 0)
            {
                var currentPartSize = Math.Min(partSize, remaining);
                var response = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = profile.BucketName,
                    Key = key,
                    UploadId = initiated.UploadId,
                    PartNumber = partNumber,
                    PartSize = currentPartSize,
                    InputStream = data,
                    DisablePayloadSigning = profile.IsR2,
                    DisableDefaultChecksumValidation = profile.IsR2,
                    UseChunkEncoding = !profile.IsR2
                }, cancellationToken);
                parts.Add(new PartETag(partNumber, response.ETag));
                remaining -= currentPartSize;
                partNumber++;
            }

            var completed = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                UploadId = initiated.UploadId,
                PartETags = parts
            }, cancellationToken);
            return completed.ETag;
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = profile.BucketName,
                    Key = key,
                    UploadId = initiated.UploadId
                }, CancellationToken.None);
            }
            catch
            {
                // Preserve the original upload error; the provider will eventually expire the orphaned upload.
            }
            throw;
        }
    }

    private sealed class S3ObjectStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public S3ObjectStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _response.ContentLength > 0 ? _response.ContentLength : _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _response.Dispose();
            await base.DisposeAsync();
        }
    }
}
