using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class UploadSessionProcessorTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IUploadEnrichmentPipeline> _pipeline = new();
    private readonly Mock<IUploadArtifactCleaner> _cleaner = new();
    private readonly DateTimeOffset _now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessAsync_Success_MakesFileReadyAndConvertsReservation()
    {
        var session = await AddProcessingSessionAsync();
        ConfigureSuccessfulPipeline(session);

        var outcome = await CreateSut().ProcessAsync(session.Id, default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        var file = _database.Context.UploadedFiles.Single(x => x.Id == session.FileId);
        stored.Status.Should().Be(UploadSessionStatus.Ready);
        stored.ReservedBytes.Should().Be(0);
        stored.ProcessingAttempts.Should().Be(1);
        file.UploadedAt.Should().Be(_now.UtcDateTime);
        outcome.Should().Be(UploadProcessingOutcome.Ready);
    }

    [Fact]
    public async Task ProcessAsync_WhenPipelineDoesNotFinalizeOriginal_FailsPermanently()
    {
        var session = await AddProcessingSessionAsync();

        var outcome = await CreateSut().ProcessAsync(session.Id, default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.ErrorCode.Should().Be("integrity_mismatch");
        outcome.Should().Be(UploadProcessingOutcome.Failed);
        _cleaner.Verify(x => x.CleanupAsync(stored, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ReplayedReadyMessage_IsNoOp()
    {
        var session = await AddProcessingSessionAsync(UploadSessionStatus.Ready);

        var outcome = await CreateSut().ProcessAsync(session.Id, default);

        _pipeline.Verify(x => x.ProcessAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()), Times.Never);
        outcome.Should().Be(UploadProcessingOutcome.NoOp);
    }

    [Fact]
    public async Task ProcessAsync_IntegrityFailure_FailsPermanentlyAndCleansArtifacts()
    {
        var session = await AddProcessingSessionAsync();
        _pipeline.Setup(x => x.ProcessAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileIntegrityException("bad sha"));

        var outcome = await CreateSut().ProcessAsync(session.Id, default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.ErrorCode.Should().Be("integrity_mismatch");
        stored.ReservedBytes.Should().Be(0);
        outcome.Should().Be(UploadProcessingOutcome.Failed);
        _cleaner.Verify(x => x.CleanupAsync(stored, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WhenCleanupFailsAfterDurableFailure_ReturnsFailedForMaintenanceRetry()
    {
        var session = await AddProcessingSessionAsync();
        _pipeline.Setup(x => x.ProcessAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileIntegrityException("bad sha"));
        _cleaner.Setup(x => x.CleanupAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database unavailable during cleanup"));

        var outcome = await CreateSut().ProcessAsync(session.Id, default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.CleanupPending.Should().BeTrue();
        outcome.Should().Be(UploadProcessingOutcome.Failed);
    }

    [Fact]
    public async Task ProcessAsync_InfrastructureFailure_RetriesFourTimesThenFailsOnFifthAttempt()
    {
        var session = await AddProcessingSessionAsync();
        _pipeline.Setup(x => x.ProcessAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("S3 unavailable"));
        var sut = CreateSut();

        for (var attempt = 1; attempt < UploadSessionProcessor.MaximumAttempts; attempt++)
        {
            var act = () => sut.ProcessAsync(session.Id, default);
            await act.Should().ThrowAsync<IOException>();
        }

        var outcome = await sut.ProcessAsync(session.Id, default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.ErrorCode.Should().Be("processing_retries_exhausted");
        stored.ErrorMessage.Should().NotContain("S3 unavailable");
        stored.ProcessingAttempts.Should().Be(UploadSessionProcessor.MaximumAttempts);
        outcome.Should().Be(UploadProcessingOutcome.Failed);
        _cleaner.Verify(x => x.CleanupAsync(stored, It.IsAny<CancellationToken>()), Times.Once);
    }

    private UploadSessionProcessor CreateSut() => new(
        _database.Context,
        _pipeline.Object,
        _cleaner.Object,
        new FixedTimeProvider(_now),
        NullLogger<UploadSessionProcessor>.Instance);

    private void ConfigureSuccessfulPipeline(UploadSession session)
    {
        _pipeline.Setup(x => x.ProcessAsync(
                It.Is<UploadSession>(s => s.Id == session.Id), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                var file = _database.Context.UploadedFiles.Single(x => x.Id == session.FileId);
                file.Etag = session.CompletedEtag;
                file.Size = session.DeclaredSize;
            })
            .Returns(Task.CompletedTask);
    }

    private async Task<UploadSession> AddProcessingSessionAsync(
        UploadSessionStatus status = UploadSessionStatus.Processing)
    {
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            OwnerId = 10,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            FileName = "movie.mp4",
            DeclaredSize = 42,
            ContentType = "video/mp4",
            Sha256 = new string('a', 64),
            Status = status,
            StorageProfileId = "universal-v1",
            MultipartUploadId = "upload-id",
            PartSize = UploadSessionCoordinator.BasePartSize,
            UploadTokenHash = string.Empty,
            ReservedBytes = status == UploadSessionStatus.Ready ? 0 : 42,
            CreatedAt = _now.UtcDateTime,
            UpdatedAt = _now.UtcDateTime,
            LastActivityAt = _now.UtcDateTime,
            ExpiresAt = _now.UtcDateTime.AddHours(24),
            CompletedEtag = "etag",
            ConcurrencyToken = Guid.NewGuid()
        };
        _database.Context.UploadSessions.Add(session);
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = session.FileId,
            Uploaders = [session.OwnerId],
            CreatedAt = _now.UtcDateTime,
            UploadedAt = status == UploadSessionStatus.Ready ? _now.UtcDateTime : null,
            Etag = status == UploadSessionStatus.Ready ? "etag" : null,
            Type = UploadFileType.CloudFile,
            StorageProfileId = session.StorageProfileId,
            Filename = session.FileName
        });
        await _database.Context.SaveChangesAsync();
        return session;
    }

    public void Dispose() => _database.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
