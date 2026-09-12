namespace BarkCloud.Files.Infrastructure;

public sealed record MultipartUploadPart(int PartNumber, long Size, string Etag);

public sealed record CompletedMultipartObject(string Etag, long Size);

public sealed record MultipartObjectInfo(string Etag, long Size);

public interface IMultipartUploadStore
{
    Task<string> InitiateAsync(
        string storageProfileId,
        string key,
        string contentType,
        string fileName,
        CancellationToken cancellationToken);

    Task<MultipartUploadPart> UploadPartAsync(
        string storageProfileId,
        string key,
        string uploadId,
        int partNumber,
        Stream body,
        long size,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultipartUploadPart>> ListPartsAsync(
        string storageProfileId,
        string key,
        string uploadId,
        CancellationToken cancellationToken);

    Task<CompletedMultipartObject> CompleteAsync(
        string storageProfileId,
        string key,
        string uploadId,
        IReadOnlyList<MultipartUploadPart> parts,
        CancellationToken cancellationToken);

    Task AbortAsync(
        string storageProfileId,
        string key,
        string uploadId,
        CancellationToken cancellationToken);

    Task<MultipartObjectInfo?> HeadAsync(
        string storageProfileId,
        string key,
        CancellationToken cancellationToken);
}
