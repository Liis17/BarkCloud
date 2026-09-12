using BarkCloud.Files.Consumers;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Files;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Consumers;

public class ProcessUploadedFileConsumerTests
{
    [Fact]
    public async Task Consume_ReplayedReadyMessage_RecordsNoOpInsteadOfSecondSuccess()
    {
        using var database = new SqliteFilesContext();
        var now = DateTime.UtcNow;
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            OwnerId = 42,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            FileName = "file.bin",
            DeclaredSize = 3,
            ContentType = "application/octet-stream",
            Sha256 = new string('a', 64),
            Status = UploadSessionStatus.Ready,
            StorageProfileId = "universal-v1",
            MultipartUploadId = "upload-id",
            PartSize = UploadSessionCoordinator.BasePartSize,
            UploadTokenHash = string.Empty,
            ReservedBytes = 0,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.AddHours(24),
            CompletedEtag = "etag",
            ConcurrencyToken = Guid.NewGuid()
        };
        database.Context.UploadSessions.Add(session);
        await database.Context.SaveChangesAsync();
        var processor = new UploadSessionProcessor(
            database.Context,
            Mock.Of<IUploadEnrichmentPipeline>(),
            Mock.Of<IUploadArtifactCleaner>(),
            TimeProvider.System,
            NullLogger<UploadSessionProcessor>.Instance);
        var metrics = new MetricsCollector();
        var sut = new ProcessUploadedFileConsumer(
            processor,
            metrics);
        var message = new ProcessUploadedFile(session.Id);
        var consumeContext = new Mock<ConsumeContext<ProcessUploadedFile>>();
        consumeContext.SetupGet(x => x.Message).Returns(message);

        await sut.Consume(consumeContext.Object);

        var snapshot = metrics.SnapshotAndReset();
        snapshot["upload_processing_received"].Should().Be(1);
        snapshot["upload_processing_noop_total"].Should().Be(1);
        snapshot.Should().NotContainKey("upload_processing_ready_total");
    }
}
