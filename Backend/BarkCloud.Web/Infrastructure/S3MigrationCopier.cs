using System.Buffers;
using System.Net;
using System.Security.Cryptography;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.GrpcServer;

namespace BarkCloud.Web.Infrastructure;

public class MigrationS3ClientFactory
{
    public virtual IAmazonS3 Create(MigrationConnection connection)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = S3Endpoint.Normalize(connection.ServiceUrl, connection.IsR2),
            ForcePathStyle = connection.ForcePathStyle,
            Timeout = TimeSpan.FromMinutes(30),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        if (!string.IsNullOrEmpty(connection.Region)) config.AuthenticationRegion = connection.Region;
        if (connection.IsR2)
        {
            config.AuthenticationRegion = "auto";
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        return new AmazonS3Client(new BasicAWSCredentials(connection.AccessKey, connection.SecretKey), config);
    }
}

/// <summary>Only reads from sourceClient. Destination uploads are verified by reading plaintext back.</summary>
public sealed class S3MigrationCopier(MigrationS3ClientFactory clients)
{
    public const long PartSize = 64L * 1024 * 1024;
    private static readonly string[] ObjectHeaders = ["Content-Type", "Content-Disposition", "Content-Encoding", "Content-Language", "Cache-Control", "Expires"];

    public static MigrationConnection Normalize(MigrationConnection connection)
    {
        if (!Uri.TryCreate(connection.ServiceUrl?.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidOperationException("Endpoint должен быть HTTP(S)-адресом без credentials, query и fragment.");
        if (string.IsNullOrWhiteSpace(connection.BucketName) || string.IsNullOrWhiteSpace(connection.AccessKey)
            || string.IsNullOrEmpty(connection.SecretKey))
            throw new InvalidOperationException("Укажите bucket, access key и secret key.");
        return connection with
        {
            ServiceUrl = S3Endpoint.Normalize(url.ToString(), connection.IsR2), BucketName = connection.BucketName.Trim(),
            AccessKey = connection.AccessKey.Trim(), Region = connection.IsR2 ? "auto" : (connection.Region ?? "").Trim()
        };
    }

    public async Task CheckAsync(MigrationConnection target, CancellationToken ct)
    {
        using var client = clients.Create(target);
        await EnsureBucketAsync(client, target.BucketName, ct);
        var key = ".barkcloud-migration-check/" + Guid.NewGuid().ToString("N");
        var content = RandomNumberGenerator.GetBytes(32);
        PutObjectResponse? written = null;
        var attempted = false;
        try
        {
            using var data = new MemoryStream(content, writable: false);
            attempted = true;
            written = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = target.BucketName, Key = key, InputStream = data,
                AutoCloseStream = false, AutoResetStreamPosition = false,
                ContentType = "application/octet-stream", DisablePayloadSigning = target.IsR2,
                DisableDefaultChecksumValidation = target.IsR2
            }, ct);
            using var read = await client.GetObjectAsync(new GetObjectRequest
            { BucketName = target.BucketName, Key = key, VersionId = written.VersionId }, ct);
            var hash = await SHA256.HashDataAsync(read.ResponseStream, ct);
            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(content)))
                throw new InvalidOperationException("Содержимое тестового объекта не совпало.");
        }
        finally
        {
            if (attempted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var version = written?.VersionId;
                var exists = true;
                if (written is null)
                {
                    try { version = (await client.GetObjectMetadataAsync(target.BucketName, key, cleanup.Token)).VersionId; }
                    catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { exists = false; }
                }
                if (exists)
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest
                    { BucketName = target.BucketName, Key = key, VersionId = version }, cleanup.Token);
                    try
                    {
                        await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                        { BucketName = target.BucketName, Key = key, VersionId = version }, cleanup.Token);
                        throw new InvalidOperationException("Не удалось удалить тестовый объект. Запуск миграции запрещён.");
                    }
                    catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { }
                }
            }
        }
    }

    public static async Task EnsureBucketAsync(IAmazonS3 client, string bucket, CancellationToken ct) =>
        await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, ct);

    public static async IAsyncEnumerable<MigrationObject> ListAsync(IAmazonS3 client, string bucket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            { BucketName = bucket, MaxKeys = 1000, ContinuationToken = continuation }, ct);
            foreach (var item in response.S3Objects ?? [])
                yield return new(item.Key, item.Size ?? 0, item.ETag ?? "", item.LastModified);
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
            if (response.IsTruncated == true && string.IsNullOrEmpty(continuation))
                throw new InvalidOperationException("S3 не вернул continuation token для следующей страницы.");
        } while (continuation is not null);
    }

    public async Task<MigrationObject> CopyAsync(IAmazonS3 sourceClient, MigrationConnection source,
        IAmazonS3 targetClient, MigrationConnection target, MigrationObject item, string directory,
        Action<MigrationProgress> progress, CancellationToken ct)
    {
        var pendingPath = Path.Combine(directory, "multipart.ndjson");
        if (File.Exists(pendingPath))
        {
            var pending = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(pendingPath, ct))!;
            await AbortPendingAsync(targetClient, target.BucketName, pending[0], pending[1], ct);
            File.Delete(pendingPath);
        }
        var response = await SourceMetadataAsync(sourceClient, source.BucketName, item, ct);
        var size = response.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var partPath = Path.Combine(directory, "part-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partSize = Math.Max(PartSize, size / 10000 + (size % 10000 == 0 ? 0 : 1));
        long uploaded = 0;
        string? uploadId = null;
        var completed = false;
        var originalName = response.Metadata["original-filename"];
        progress(new(0, "copying", originalName));
        try
        {
            if (size <= PartSize)
            {
                await using var part = new FileStream(partPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
                await DownloadRangeAsync(sourceClient, source.BucketName, item.Key, response.ETag, 0, size, size, part, true, ct);
                await AppendHashAsync(part, hash, ct);
                var request = new PutObjectRequest
                {
                    BucketName = target.BucketName, Key = item.Key, InputStream = part,
                    AutoCloseStream = false, AutoResetStreamPosition = false,
                    DisablePayloadSigning = target.IsR2, DisableDefaultChecksumValidation = target.IsR2
                };
                CopyHeaders(response.Metadata, response.Headers, request.Headers, request.Metadata);
                request.StreamTransferProgress += (_, args) => progress(new(Math.Min(size, args.TransferredBytes), "copying", originalName));
                await targetClient.PutObjectAsync(request, ct);
                uploaded = size;
            }
            else
            {
                var initiation = new InitiateMultipartUploadRequest { BucketName = target.BucketName, Key = item.Key };
                CopyHeaders(response.Metadata, response.Headers, initiation.Headers, initiation.Metadata);
                uploadId = (await targetClient.InitiateMultipartUploadAsync(initiation, ct)).UploadId;
                await File.WriteAllTextAsync(pendingPath, System.Text.Json.JsonSerializer.Serialize(new[] { item.Key, uploadId }), ct);
                var parts = new List<PartETag>();
                for (var number = 1; uploaded < size; number++)
                {
                    var length = Math.Min(partSize, size - uploaded);
                    await using var part = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
                    progress(new(uploaded, "reading", originalName));
                    await DownloadRangeAsync(sourceClient, source.BucketName, item.Key, response.ETag, uploaded, length, size, part, true, ct);
                    await AppendHashAsync(part, hash, ct);
                    progress(new(uploaded, "copying", originalName));
                    var baseline = uploaded;
                    var request = new UploadPartRequest
                    {
                        BucketName = target.BucketName, Key = item.Key, UploadId = uploadId, PartNumber = number,
                        PartSize = length, InputStream = part, DisablePayloadSigning = target.IsR2,
                        DisableDefaultChecksumValidation = target.IsR2, UseChunkEncoding = !target.IsR2
                    };
                    request.StreamTransferProgress += (_, args) => progress(new(baseline + Math.Min(length, args.TransferredBytes), "copying", originalName));
                    var result = await targetClient.UploadPartAsync(request, ct);
                    parts.Add(new PartETag(number, result.ETag));
                    uploaded += length;
                    progress(new(uploaded, "copying", originalName));
                }
                await targetClient.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                { BucketName = target.BucketName, Key = item.Key, UploadId = uploadId, PartETags = parts }, ct);
                File.Delete(pendingPath);
            }
            completed = true;
            var sourceHash = hash.GetHashAndReset();
            progress(new(uploaded, "verifying", originalName));
            var copy = await targetClient.GetObjectMetadataAsync(target.BucketName, item.Key, ct);
            var targetHash = await HashObjectAsync(targetClient, target.BucketName, item.Key, copy.ContentLength, copy.ETag, partPath, false, ct);
            if (copy.ContentLength != size || !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                throw new InvalidOperationException("Контрольная сумма или размер копии не совпали с источником.");
            foreach (var key in response.Metadata.Keys)
                if (response.Metadata[key] != copy.Metadata[key])
                    throw new InvalidOperationException("Метаданные копии не совпали с источником.");
            foreach (var key in ObjectHeaders)
                if (!string.IsNullOrEmpty(response.Headers[key]) && response.Headers[key] != copy.Headers[key])
                    throw new InvalidOperationException("Заголовки копии не совпали с источником.");
            // ListObjects preserves subsecond timestamps which Last-Modified HTTP headers lose.
            return new(item.Key, size, response.ETag ?? "", item.LastModified ?? response.LastModified, Convert.ToHexString(sourceHash),
                MetadataHash(response.Metadata, response.Headers));
        }
        finally
        {
            try
            {
                if (!completed && uploadId is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await AbortPendingAsync(targetClient, target.BucketName, item.Key, uploadId, cleanup.Token);
                    File.Delete(pendingPath);
                }
            }
            finally { File.Delete(partPath); }
        }
    }

    public async Task<MigrationObject?> VerifyExistingAsync(IAmazonS3 sourceClient, MigrationConnection source,
        IAmazonS3 targetClient, MigrationConnection target, MigrationObject item, string directory,
        Action<MigrationProgress> progress, CancellationToken ct)
    {
        GetObjectMetadataResponse existing;
        try { existing = await targetClient.GetObjectMetadataAsync(target.BucketName, item.Key, ct); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
        var original = await SourceMetadataAsync(sourceClient, source.BucketName, item, ct);
        if (original.ContentLength != existing.ContentLength || MetadataHash(original.Metadata, original.Headers) != MetadataHash(existing.Metadata, existing.Headers))
            return null;
        Directory.CreateDirectory(directory);
        var partPath = Path.Combine(directory, "verify-" + Guid.NewGuid().ToString("N"));
        progress(new(0, "checking-existing", original.Metadata["original-filename"]));
        try
        {
            var sourceHash = await HashObjectAsync(sourceClient, source.BucketName, item.Key, original.ContentLength, original.ETag, partPath, true, ct);
            var targetHash = await HashObjectAsync(targetClient, target.BucketName, item.Key, existing.ContentLength, existing.ETag, partPath, false, ct);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash)) return null;
            return new(item.Key, original.ContentLength, original.ETag ?? "", item.LastModified ?? original.LastModified,
                Convert.ToHexString(sourceHash), MetadataHash(original.Metadata, original.Headers));
        }
        finally { File.Delete(partPath); }
    }

    public static string MetadataHash(MetadataCollection metadata, HeadersCollection headers) => Convert.ToHexString(
        SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Metadata = metadata.Keys.Order(StringComparer.Ordinal).Select(key => new[] { key, metadata[key] }),
            Headers = ObjectHeaders.Where(key => !string.IsNullOrEmpty(headers[key])).Select(key => new[] { key, headers[key] })
        })));

    private static async Task AbortPendingAsync(IAmazonS3 client, string bucket, string key, string uploadId, CancellationToken ct)
    {
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            { BucketName = bucket, Key = key, UploadId = uploadId }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload" || e.StatusCode == HttpStatusCode.NotFound) { }
    }

    private static void CopyHeaders(MetadataCollection sourceMetadata, HeadersCollection sourceHeaders, HeadersCollection headers, MetadataCollection metadata)
    {
        foreach (var key in ObjectHeaders)
            if (!string.IsNullOrEmpty(sourceHeaders[key])) headers[key] = sourceHeaders[key];
        foreach (var key in sourceMetadata.Keys) metadata[key] = sourceMetadata[key];
    }

    private static async Task<GetObjectMetadataResponse> SourceMetadataAsync(IAmazonS3 client, string bucket, MigrationObject item, CancellationToken ct)
    {
        GetObjectMetadataResponse response;
        try { response = await client.GetObjectMetadataAsync(bucket, item.Key, ct); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { throw new MigrationSourceMissingException(); }
        if (!string.IsNullOrEmpty(item.Etag) && item.Etag != response.ETag)
            throw new AmazonS3Exception("Source changed") { StatusCode = HttpStatusCode.PreconditionFailed };
        return response;
    }

    private static async Task<byte[]> HashObjectAsync(IAmazonS3 client, string bucket, string key, long size, string? etag,
        string partPath, bool source, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < size; offset += PartSize)
        {
            var length = Math.Min(PartSize, size - offset);
            await using var part = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            await DownloadRangeAsync(client, bucket, key, etag, offset, length, size, part, source, ct);
            await AppendHashAsync(part, hash, ct);
        }
        return hash.GetHashAndReset();
    }

    private static async Task DownloadRangeAsync(IAmazonS3 client, string bucket, string key, string? etag, long offset,
        long length, long size, Stream part, bool source, CancellationToken ct)
    {
        if (length == 0) { part.Position = 0; return; }
        for (var attempt = 0; ; attempt++)
        {
            part.SetLength(0); part.Position = 0;
            try
            {
                var request = new GetObjectRequest { BucketName = bucket, Key = key, EtagToMatch = etag,
                    ByteRange = new ByteRange(offset, offset + length - 1) };
                using var response = source ? await ReadSourceAsync(client, request, ct) : await client.GetObjectAsync(request, ct);
                if (response.ContentLength != length || response.ContentRange != $"bytes {offset}-{offset + length - 1}/{size}")
                    throw new InvalidOperationException("S3 вернул неверный размер или диапазон объекта.");
                await ReadPartAsync(response.ResponseStream, part, length, ct);
                return;
            }
            catch (Exception e) when (attempt < 2 && !ct.IsCancellationRequested &&
                (e is IOException or HttpRequestException or TimeoutException or OperationCanceledException
                    || e is AmazonS3Exception s3 && ((int)s3.StatusCode >= 500 || s3.ErrorCode is "RequestTimeout" or "RequestTimeoutException")))
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }

    private static async Task<GetObjectResponse> ReadSourceAsync(IAmazonS3 client, GetObjectRequest request, CancellationToken ct)
    {
        try { return await client.GetObjectAsync(request, ct); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { throw new MigrationSourceMissingException(); }
    }

    private static async Task ReadPartAsync(Stream source, Stream part, long length, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            for (long read = 0; read < length;)
            {
                idle.CancelAfter(TimeSpan.FromMinutes(5));
                int count;
                try { count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - read)), idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("S3 read stalled"); }
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0) throw new InvalidOperationException("Контрольная сумма или размер копии не совпали: поток завершился раньше ожидаемого.");
                await part.WriteAsync(buffer.AsMemory(0, count), ct);
                read += count;
            }
            await part.FlushAsync(ct);
            part.Position = 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task AppendHashAsync(Stream part, IncrementalHash hash, CancellationToken ct)
    {
        part.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int count;
            while ((count = await part.ReadAsync(buffer, ct)) != 0) hash.AppendData(buffer, 0, count);
            part.Position = 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
