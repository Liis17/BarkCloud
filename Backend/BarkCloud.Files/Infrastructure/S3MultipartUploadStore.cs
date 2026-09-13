using System.Net;
using System.Globalization;

using Amazon.S3;
using Amazon.S3.Model;

namespace BarkCloud.Files.Infrastructure;

public sealed class S3MultipartUploadStore : IMultipartUploadStore
{
    private readonly S3BucketRegistry _registry;

    public S3MultipartUploadStore(S3BucketRegistry registry)
    {
        _registry = registry;
    }

    public async Task<string> InitiateAsync(
        string storageProfileId,
        string key,
        string contentType,
        string fileName,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var response = await _registry.GetClientForProfile(storageProfileId)
            .InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                ContentType = contentType,
                Metadata = { ["original-filename"] = Path.GetFileName(fileName) }
            }, cancellationToken);
        return response.UploadId;
    }

    public async Task<MultipartUploadPart> UploadPartAsync(
        string storageProfileId,
        string key,
        string uploadId,
        int partNumber,
        Stream body,
        long size,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var response = await _registry.GetClientForProfile(storageProfileId)
            .UploadPartAsync(new UploadPartRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                PartSize = size,
                DisablePayloadSigning = profile.IsR2,
                InputStream = body,
                DisableDefaultChecksumValidation = profile.IsR2
            }, cancellationToken);
        return new MultipartUploadPart(partNumber, size, response.ETag);
    }

    public async Task<IReadOnlyList<MultipartUploadPart>> ListPartsAsync(
        string storageProfileId,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var client = _registry.GetClientForProfile(storageProfileId);
        var result = new List<MultipartUploadPart>();
        string? marker = null;

        do
        {
            var response = await client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                UploadId = uploadId,
                PartNumberMarker = marker
            }, cancellationToken);
            result.AddRange(response.Parts.Select(x => new MultipartUploadPart(
                x.PartNumber ?? 0,
                x.Size ?? 0,
                x.ETag)));
            marker = response.IsTruncated == true && response.NextPartNumberMarker is int nextMarker
                ? nextMarker.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        while (marker is not null);

        return result.OrderBy(x => x.PartNumber).ToArray();
    }

    public async Task<CompletedMultipartObject> CompleteAsync(
        string storageProfileId,
        string key,
        string uploadId,
        IReadOnlyList<MultipartUploadPart> parts,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        var response = await _registry.GetClientForProfile(storageProfileId)
            .CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                UploadId = uploadId,
                PartETags = parts.Select(x => new PartETag(x.PartNumber, x.Etag)).ToList()
            }, cancellationToken);
        return new CompletedMultipartObject(response.ETag, parts.Sum(x => x.Size));
    }

    public async Task AbortAsync(
        string storageProfileId,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        await _registry.GetClientForProfile(storageProfileId)
            .AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = profile.BucketName,
                Key = key,
                UploadId = uploadId
            }, cancellationToken);
    }

    public async Task<MultipartObjectInfo?> HeadAsync(
        string storageProfileId,
        string key,
        CancellationToken cancellationToken)
    {
        var profile = _registry.GetProfile(storageProfileId);
        try
        {
            var response = await _registry.GetClientForProfile(storageProfileId)
                .GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = profile.BucketName,
                    Key = key
                }, cancellationToken);
            return new MultipartObjectInfo(response.ETag, response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
