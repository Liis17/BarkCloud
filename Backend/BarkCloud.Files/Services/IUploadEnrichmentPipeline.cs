using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Services;

public interface IUploadEnrichmentPipeline
{
    Task ProcessAsync(UploadSession session, CancellationToken cancellationToken);
}
