namespace BarkCloud.Files.Services;

public interface IUploadProcessingPublisher
{
    Task PublishAsync(Guid sessionId, CancellationToken cancellationToken);
}
