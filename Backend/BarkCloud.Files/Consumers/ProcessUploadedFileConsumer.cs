using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Files;

using MassTransit;

namespace BarkCloud.Files.Consumers;

public sealed class ProcessUploadedFileConsumer(
    UploadSessionProcessor processor,
    MetricsCollector metrics) : IConsumer<ProcessUploadedFile>
{
    public async Task Consume(ConsumeContext<ProcessUploadedFile> context)
    {
        metrics.Increment("upload_processing_received");
        try
        {
            var outcome = await processor.ProcessAsync(context.Message.SessionId, context.CancellationToken);
            metrics.Increment(outcome switch
            {
                UploadProcessingOutcome.Ready => "upload_processing_ready_total",
                UploadProcessingOutcome.Failed => "upload_processing_failed_total",
                _ => "upload_processing_noop_total"
            });
        }
        catch
        {
            metrics.Increment("upload_processing_retry");
            throw;
        }
    }
}
