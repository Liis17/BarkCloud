using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class UploadSessionMaintenanceTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IMultipartUploadStore> _objects = new();
    private readonly Mock<IUploadArtifactCleaner> _cleaner = new();
    private readonly DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_ExpiresInactiveUploadAndReleasesReservation()
    {
        var session = AddSession(UploadSessionStatus.Uploading, _now.UtcDateTime.AddMinutes(-1));
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Expired);
        stored.ReservedBytes.Should().Be(0);
        _database.Context.UploadedFiles.Should().BeEmpty();
        _objects.Verify(x => x.AbortAsync(
            session.StorageProfileId,
            session.FileId.ToString(),
            session.MultipartUploadId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_RemovesTerminalSessionAfterSevenDays()
    {
        var session = AddSession(
            UploadSessionStatus.Cancelled,
            _now.UtcDateTime.AddDays(-8),
            addPlaceholder: false);
        session.UpdatedAt = _now.UtcDateTime.AddDays(-8);
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        _database.Context.UploadSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task RunOnceAsync_AfterDeferredAbortSucceeds_RemovesPlaceholder()
    {
        var session = AddSession(
            UploadSessionStatus.Cancelled,
            _now.UtcDateTime.AddHours(1));
        session.CleanupPending = true;
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.CleanupPending.Should().BeFalse();
        _database.Context.UploadedFiles.Should().BeEmpty();
        _objects.Verify(x => x.AbortAsync(
            session.StorageProfileId,
            session.FileId.ToString(),
            session.MultipartUploadId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_WhenMultipartWasAlreadyCompleted_UsesArtifactCleaner()
    {
        var session = AddSession(
            UploadSessionStatus.Cancelled,
            _now.UtcDateTime.AddHours(1));
        session.CleanupPending = true;
        await _database.Context.SaveChangesAsync();
        _objects.Setup(x => x.AbortAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                session.MultipartUploadId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("multipart no longer exists"));
        _objects.Setup(x => x.HeadAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartObjectInfo("etag", 100));

        await CreateSut().RunOnceAsync(default);

        _cleaner.Verify(x => x.CleanupAsync(
            It.Is<UploadSession>(s => s.Id == session.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_ReleasesExpiredLegacyProcessingReservationAfterRestart()
    {
        var session = AddSession(
            UploadSessionStatus.Processing,
            _now.UtcDateTime.AddMinutes(-1));
        session.MultipartUploadId = string.Empty;
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.ReservedBytes.Should().Be(0);
        stored.ErrorCode.Should().Be("legacy_upload_expired");
        _cleaner.Verify(x => x.CleanupAsync(
            It.Is<UploadSession>(s => s.Id == session.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_FinalizesPersistedLegacyUploadAfterRestart()
    {
        var session = AddSession(
            UploadSessionStatus.Processing,
            _now.UtcDateTime.AddMinutes(-1));
        session.MultipartUploadId = string.Empty;
        session.LegacyProcessingCompletedAt = _now.UtcDateTime.AddMinutes(-2);
        var file = _database.Context.UploadedFiles.Local.Single(x => x.Id == session.FileId);
        file.Etag = "etag";
        file.Size = session.DeclaredSize;
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Ready);
        stored.ReservedBytes.Should().Be(0);
        file.UploadedAt.Should().Be(_now.UtcDateTime);
        _cleaner.Verify(x => x.CleanupAsync(
            It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotPublishLegacyObjectWithoutCompletionMarker()
    {
        var session = AddSession(
            UploadSessionStatus.Processing,
            _now.UtcDateTime.AddMinutes(-1));
        session.MultipartUploadId = string.Empty;
        var file = _database.Context.UploadedFiles.Local.Single(x => x.Id == session.FileId);
        file.Etag = "etag";
        file.Size = session.DeclaredSize;
        await _database.Context.SaveChangesAsync();

        await CreateSut().RunOnceAsync(default);

        var stored = _database.Context.UploadSessions.Single(x => x.Id == session.Id);
        stored.Status.Should().Be(UploadSessionStatus.Failed);
        stored.ReservedBytes.Should().Be(0);
        stored.CleanupPending.Should().BeTrue();
        file.UploadedAt.Should().BeNull();
        _cleaner.Verify(x => x.CleanupAsync(
            It.Is<UploadSession>(s => s.Id == session.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private UploadSessionMaintenance CreateSut() => new(
        _database.Context,
        _objects.Object,
        _cleaner.Object,
        new FixedTimeProvider(_now),
        NullLogger<UploadSessionMaintenance>.Instance);

    private UploadSession AddSession(
        UploadSessionStatus status,
        DateTime expiresAt,
        bool addPlaceholder = true)
    {
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            OwnerId = 42,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            FileName = "file.bin",
            DeclaredSize = 100,
            ContentType = "application/octet-stream",
            Sha256 = new string('a', 64),
            Status = status,
            StorageProfileId = "universal-v1",
            MultipartUploadId = "upload-id",
            PartSize = UploadSessionCoordinator.BasePartSize,
            UploadTokenHash = "hash",
            ReservedBytes = 100,
            CreatedAt = _now.UtcDateTime.AddDays(-1),
            UpdatedAt = _now.UtcDateTime.AddDays(-1),
            LastActivityAt = _now.UtcDateTime.AddDays(-1),
            ExpiresAt = expiresAt,
            ConcurrencyToken = Guid.NewGuid()
        };
        _database.Context.UploadSessions.Add(session);
        if (addPlaceholder)
        {
            _database.Context.UploadedFiles.Add(new UploadFile
            {
                Id = session.FileId,
                Uploaders = [session.OwnerId],
                CreatedAt = session.CreatedAt,
                Type = UploadFileType.CloudFile,
                StorageProfileId = session.StorageProfileId
            });
        }
        return session;
    }

    public void Dispose() => _database.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
