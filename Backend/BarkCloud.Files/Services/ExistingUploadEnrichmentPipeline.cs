using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Infrastructure;

using MediatR;

namespace BarkCloud.Files.Services;

public sealed class ExistingUploadEnrichmentPipeline(S3Uploader s3, ISender sender)
    : IUploadEnrichmentPipeline
{
    public async Task ProcessAsync(Domain.UploadSession session, CancellationToken cancellationToken)
    {
        await using var original = await s3.DownloadAsync(
            session.StorageProfileId,
            session.FileId.ToString(),
            cancellationToken);

        await sender.Send(new UploadFileCommand
        {
            FileId = session.FileId,
            FileStream = original,
            FileName = session.FileName,
            ContentTypeOverride = session.ContentType,
            FileSize = session.DeclaredSize,
            OriginalAlreadyStored = true,
            StoredEtag = session.CompletedEtag,
            ExpectedSha256 = session.Sha256,
            StorageProfileIdOverride = session.StorageProfileId,
            ForceDiskBuffer = true,
            DeferAvailability = true
        }, cancellationToken);
    }
}
