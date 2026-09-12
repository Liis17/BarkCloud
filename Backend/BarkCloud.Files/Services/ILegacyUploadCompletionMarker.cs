namespace BarkCloud.Files.Services;

public interface ILegacyUploadCompletionMarker
{
    Task MarkProcessingCompletedAsync(Guid sessionId, CancellationToken cancellationToken);
}
