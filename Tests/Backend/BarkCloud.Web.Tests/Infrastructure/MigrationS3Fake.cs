using System.Net;
using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

internal sealed class MigrationS3Fake
{
    internal sealed record Item(byte[] Data, string Etag, DateTime Modified, Dictionary<string, string> Metadata,
        Dictionary<string, string> Headers, string? Version);
    public Mock<IAmazonS3> Client { get; } = new(MockBehavior.Strict);
    public SortedDictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> Written { get; } = [];
    public List<DeleteObjectRequest> Deleted { get; } = [];
    public bool Versioned { get; set; }
    public bool FailCleanup { get; set; }
    public string? FailPutKey { get; set; }
    public bool CorruptRead { get; set; }
    public string? CorruptReadKey { get; set; }
    public Action? OnList { get; set; }
    public int PageSize { get; set; } = 1000;
    public int OpenReads { get; private set; }
    public List<GetObjectRequest> Reads { get; } = [];
    public Action? OnUploadPart { get; set; }
    public Action<string>? OnPut { get; set; }
    public long? DisconnectReadOffsetOnce { get; set; }
    public Exception? FailReadOnce { get; set; }
    private readonly Dictionary<string, (InitiateMultipartUploadRequest Request, List<byte[]> Parts)> _multipart = [];

    public MigrationS3Fake(bool readOnly = false)
    {
        Client.Setup(x => x.Dispose());
        Client.Setup(x => x.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ListObjectsV2Request request, CancellationToken _) =>
            {
                OnList?.Invoke();
                var skip = int.TryParse(request.ContinuationToken, out var offset) ? offset : 0;
                var entries = Objects.Skip(skip).Take(Math.Min(PageSize, request.MaxKeys ?? 1000)).ToArray();
                return new ListObjectsV2Response
                {
                    S3Objects = entries.Select(x => new S3Object { Key = x.Key, Size = x.Value.Data.LongLength, ETag = x.Value.Etag, LastModified = x.Value.Modified }).ToList(),
                    IsTruncated = skip + entries.Length < Objects.Count,
                    NextContinuationToken = (skip + entries.Length).ToString()
                };
            });
        Client.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetObjectRequest request, CancellationToken _) =>
            {
                if (FailReadOnce is { } failure) { FailReadOnce = null; throw failure; }
                var item = Find(request.Key);
                if (!string.IsNullOrEmpty(request.EtagToMatch) && request.EtagToMatch != item.Etag) throw Error(HttpStatusCode.PreconditionFailed);
                var start = request.ByteRange?.Start ?? 0;
                var end = request.ByteRange is null ? item.Data.LongLength - 1 : Math.Min(request.ByteRange.End, item.Data.LongLength - 1);
                Reads.Add(request); OpenReads++;
                var length = Math.Max(0, end - start + 1);
                var disconnect = request.ByteRange is not null && DisconnectReadOffsetOnce == start;
                if (disconnect) DisconnectReadOffsetOnce = null;
                var stream = CorruptRead || request.Key == CorruptReadKey
                    ? new TrackedStream([42], 0, 1, () => OpenReads--)
                    : new TrackedStream(item.Data, checked((int)start), checked((int)length), () => OpenReads--, disconnect);
                var response = new GetObjectResponse { ContentLength = length, ETag = item.Etag,
                    HttpStatusCode = request.ByteRange is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent,
                    ContentRange = request.ByteRange is null ? null : $"bytes {start}-{end}/{item.Data.LongLength}",
                    LastModified = item.Modified, ResponseStream = stream, VersionId = item.Version };
                foreach (var pair in item.Metadata) response.Metadata[pair.Key] = pair.Value;
                foreach (var pair in item.Headers) response.Headers[pair.Key] = pair.Value;
                return response;
            });
        Client.Setup(x => x.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetObjectMetadataRequest request, CancellationToken _) => Metadata(request.Key));
        Client.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken _) => Metadata(key));
        if (readOnly) return; // Any attempted source write fails the test at the SDK boundary.
        Client.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (PutObjectRequest request, CancellationToken ct) =>
            {
                if (request.Key == FailPutKey) throw Error(HttpStatusCode.ServiceUnavailable);
                OnPut?.Invoke(request.Key);
                using var output = new MemoryStream();
                await request.InputStream.CopyToAsync(output, ct);
                Put(request.Key, output.ToArray(), request.Metadata.Keys.ToDictionary(x => x, x => request.Metadata[x]),
                    request.Headers.Keys.ToDictionary(x => x, x => request.Headers[x]));
                Written.Add(request.Key);
                return new PutObjectResponse { ETag = Objects[request.Key].Etag, VersionId = Objects[request.Key].Version };
            });
        Client.Setup(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InitiateMultipartUploadRequest request, CancellationToken _) =>
            { var id = Guid.NewGuid().ToString(); _multipart[id] = (request, []); return new InitiateMultipartUploadResponse { UploadId = id }; });
        Client.Setup(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (UploadPartRequest request, CancellationToken ct) =>
            {
                OnUploadPart?.Invoke(); using var data = new MemoryStream(); await request.InputStream.CopyToAsync(data, ct);
                _multipart[request.UploadId].Parts.Add(data.ToArray());
                return new UploadPartResponse { ETag = "part-" + request.PartNumber };
            });
        Client.Setup(x => x.CompleteMultipartUploadAsync(It.IsAny<CompleteMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CompleteMultipartUploadRequest request, CancellationToken _) =>
            {
                var upload = _multipart[request.UploadId]; using var data = new MemoryStream();
                foreach (var part in upload.Parts) data.Write(part);
                Put(request.Key, data.ToArray(), upload.Request.Metadata.Keys.ToDictionary(x => x, x => upload.Request.Metadata[x]),
                    upload.Request.Headers.Keys.ToDictionary(x => x, x => upload.Request.Headers[x]));
                Written.Add(request.Key); _multipart.Remove(request.UploadId);
                return new CompleteMultipartUploadResponse();
            });
        Client.Setup(x => x.AbortMultipartUploadAsync(It.IsAny<AbortMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AbortMultipartUploadRequest request, CancellationToken _) =>
            { _multipart.Remove(request.UploadId); return new AbortMultipartUploadResponse(); });
        Client.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeleteObjectRequest request, CancellationToken _) =>
            {
                if (FailCleanup) throw Error(HttpStatusCode.Forbidden);
                Deleted.Add(request); Objects.Remove(request.Key);
                return new DeleteObjectResponse();
            });
    }

    public void Put(string key, byte[] data, Dictionary<string, string>? metadata = null, Dictionary<string, string>? headers = null) =>
        Objects[key] = new(data, '"' + Convert.ToHexString(MD5.HashData(data)) + '"', DateTime.UtcNow,
            (metadata ?? []).ToDictionary(x => x.Key.StartsWith("x-amz-meta-") ? x.Key : "x-amz-meta-" + x.Key, x => x.Value),
            headers ?? new() { ["Content-Type"] = "application/octet-stream" }, Versioned ? Guid.NewGuid().ToString() : null);
    private Item Find(string key) => Objects.TryGetValue(key, out var item) ? item : throw Error(HttpStatusCode.NotFound);
    private GetObjectMetadataResponse Metadata(string key)
    {
        var item = Find(key);
        var response = new GetObjectMetadataResponse { ContentLength = item.Data.LongLength, ETag = item.Etag, LastModified = item.Modified, VersionId = item.Version };
        foreach (var pair in item.Metadata) response.Metadata[pair.Key] = pair.Value;
        foreach (var pair in item.Headers) response.Headers[pair.Key] = pair.Value;
        return response;
    }
    public static AmazonS3Exception Error(HttpStatusCode code) => new("A deliberately untrusted SDK message") { StatusCode = code };

    private sealed class TrackedStream(byte[] data, int offset, int length, Action closed, bool disconnect = false) : MemoryStream(data, offset, length, false)
    {
        private bool _closed;
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (disconnect && _reads++ == 1) throw new IOException("Simulated broken S3 connection after a partial read");
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        { if (!_closed) { _closed = true; closed(); } base.Dispose(disposing); }
    }
}

internal sealed class MigrationFakeClients(MigrationS3Fake source, MigrationS3Fake target) : MigrationS3ClientFactory
{
    public override IAmazonS3 Create(MigrationConnection connection) => connection.ServiceUrl.Contains("source") ? source.Client.Object : target.Client.Object;
}
