using BarkCloud.Shared.Queue.Files;

using MassTransit;

namespace BarkCloud.Files.Services;

public sealed class MassTransitUploadProcessingPublisher(IPublishEndpoint publishEndpoint)
    : IUploadProcessingPublisher
{
    public Task PublishAsync(Guid sessionId, CancellationToken cancellationToken) =>
        publishEndpoint.Publish(new ProcessUploadedFile(sessionId), cancellationToken);
}
