using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Services;

public interface IUploadArtifactCleaner
{
    Task CleanupAsync(UploadSession session, CancellationToken cancellationToken);
}
