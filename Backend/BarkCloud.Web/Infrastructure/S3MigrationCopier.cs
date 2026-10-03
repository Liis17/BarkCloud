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
            ForcePathStyle = connection.ForcePathStyle
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
        await EnsureEmptyAsync(client, target.BucketName, ct);
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
        await EnsureEmptyAsync(client, target.BucketName, ct);
    }

    public static async Task EnsureEmptyAsync(IAmazonS3 client, string bucket, CancellationToken ct)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, ct);
        if (response.S3Objects?.Count > 0)
            throw new InvalidOperationException("Целевой бакет должен быть пустым. Существующие объекты не удаляются.");
    }

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
        using var response = await ReadSourceAsync(sourceClient, new GetObjectRequest
        { BucketName = source.BucketName, Key = item.Key, EtagToMatch = string.IsNullOrEmpty(item.Etag) ? null : item.Etag }, ct);
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
                await ReadPartAsync(response.ResponseStream, part, size, hash, ct);
                var request = new PutObjectRequest
                {
                    BucketName = target.BucketName, Key = item.Key, InputStream = part,
                    AutoCloseStream = false, AutoResetStreamPosition = false,
                    DisablePayloadSigning = target.IsR2, DisableDefaultChecksumValidation = target.IsR2
                };
                CopyHeaders(response, request.Headers, request.Metadata);
                request.StreamTransferProgress += (_, args) => progress(new(Math.Min(size, args.TransferredBytes), "copying", originalName));
                await targetClient.PutObjectAsync(request, ct);
                uploaded = size;
            }
            else
            {
                var initiation = new InitiateMultipartUploadRequest { BucketName = target.BucketName, Key = item.Key };
                CopyHeaders(response, initiation.Headers, initiation.Metadata);
                uploadId = (await targetClient.InitiateMultipartUploadAsync(initiation, ct)).UploadId;
                await File.WriteAllTextAsync(pendingPath, System.Text.Json.JsonSerializer.Serialize(new[] { item.Key, uploadId }), ct);
                var parts = new List<PartETag>();
                for (var number = 1; uploaded < size; number++)
                {
                    var length = Math.Min(partSize, size - uploaded);
                    await using var part = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
                    await ReadPartAsync(response.ResponseStream, part, length, hash, ct);
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
            using var copy = await targetClient.GetObjectAsync(new GetObjectRequest { BucketName = target.BucketName, Key = item.Key }, ct);
            var targetHash = await SHA256.HashDataAsync(copy.ResponseStream, ct);
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

    private static void CopyHeaders(GetObjectResponse source, HeadersCollection headers, MetadataCollection metadata)
    {
        foreach (var key in ObjectHeaders)
            if (!string.IsNullOrEmpty(source.Headers[key])) headers[key] = source.Headers[key];
        foreach (var key in source.Metadata.Keys) metadata[key] = source.Metadata[key];
    }

    private static async Task<GetObjectResponse> ReadSourceAsync(IAmazonS3 client, GetObjectRequest request, CancellationToken ct)
    {
        try { return await client.GetObjectAsync(request, ct); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { throw new MigrationSourceMissingException(); }
    }

    private static async Task ReadPartAsync(Stream source, Stream part, long length, IncrementalHash hash, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (long read = 0; read < length;)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - read)), ct);
                if (count == 0) throw new InvalidOperationException("Источник завершился до заявленного размера объекта.");
                hash.AppendData(buffer, 0, count);
                await part.WriteAsync(buffer.AsMemory(0, count), ct);
                read += count;
            }
            await part.FlushAsync(ct);
            part.Position = 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
