using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

namespace BarkCloud.Files.Tests.Services;

public class LegacyUploadQuotaGuardTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageQuotaService> _quota = new();

    [Fact]
    public async Task ReserveAsync_ChecksQuotaBeforeLegacyProcessingAndPersistsReservation()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        _quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(100, 80, 0));

        var act = () => new LegacyUploadQuotaGuard(
                _database.Context, _quota.Object, TimeProvider.System)
            .ReserveAsync(fileId, "legacy.bin", 21, default);

        await act.Should().ThrowAsync<UploadQuotaExceededException>();
        _database.Context.UploadSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_AtomicallyMakesFileReadyAndReleasesLegacyReservation()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        _quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var sut = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System);

        var reservation = await sut.ReserveAsync(fileId, "legacy.bin", 21, default);
        var file = _database.Context.UploadedFiles.Single(x => x.Id == fileId);
        file.Etag = "etag";
        file.Size = 21;
        await _database.Context.SaveChangesAsync();
        await sut.MarkProcessingCompletedAsync(reservation.SessionId, default);
        await sut.CompleteAsync(reservation.SessionId, default);

        var session = _database.Context.UploadSessions.Single(x => x.Id == reservation.SessionId);
        session.Status.Should().Be(UploadSessionStatus.Ready);
        session.ReservedBytes.Should().Be(0);
        file.UploadedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReserveAsync_WhenOriginalWasPersistedBeforeLostComplete_FinalizesReplay()
    {
        var fileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            Filename = "legacy.bin",
            Size = 21,
            Etag = "etag",
            UploadedAt = null,
            CreatedAt = now,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        _database.Context.UploadSessions.Add(new UploadSession
        {
            Id = sessionId,
            FileId = fileId,
            OwnerId = 42,
            IdempotencyKey = $"legacy:{fileId:N}",
            FileName = "legacy.bin",
            DeclaredSize = 21,
            ContentType = "application/octet-stream",
            Sha256 = string.Empty,
            Status = UploadSessionStatus.Processing,
            StorageProfileId = "universal-v1",
            MultipartUploadId = string.Empty,
            PartSize = 21,
            UploadTokenHash = string.Empty,
            ReservedBytes = 21,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.AddHours(24),
            LegacyProcessingCompletedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        });
        await _database.Context.SaveChangesAsync();
        var sut = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System);

        await sut.ReserveAsync(fileId, "legacy.bin", 21, default);

        var session = _database.Context.UploadSessions.Single(x => x.Id == sessionId);
        session.Status.Should().Be(UploadSessionStatus.Ready);
        session.ReservedBytes.Should().Be(0);
        _database.Context.UploadedFiles.Single(x => x.Id == fileId).UploadedAt.Should().NotBeNull();
        _quota.Verify(
            x => x.GetSnapshotAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReserveAsync_WhenLegacyHandlerIsStillRunning_RejectsParallelProcessing()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        _quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var sut = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System);
        await sut.ReserveAsync(fileId, "legacy.bin", 21, default);

        var act = () => sut.ReserveAsync(fileId, "legacy.bin", 21, default);

        await act.Should().ThrowAsync<FileAlreadyUploadedException>()
            .WithMessage("*выполняется*");
        _database.Context.UploadSessions.Should().ContainSingle();
        _database.Context.UploadSessions.Single().Status.Should().Be(UploadSessionStatus.Processing);
    }

    [Fact]
    public async Task FailAsync_AfterOriginalWasPersisted_SchedulesArtifactCleanup()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        _quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var sut = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System);
        var reservation = await sut.ReserveAsync(fileId, "legacy.bin", 21, default);
        var file = _database.Context.UploadedFiles.Single(x => x.Id == fileId);
        file.Etag = "etag";
        file.Size = 21;
        await _database.Context.SaveChangesAsync();

        await sut.FailAsync(reservation.SessionId, new IOException("post-persist failure"), default);

        var session = _database.Context.UploadSessions.Single(x => x.Id == reservation.SessionId);
        session.Status.Should().Be(UploadSessionStatus.Failed);
        session.ReservedBytes.Should().Be(0);
        session.CleanupPending.Should().BeTrue();
        file.UploadedAt.Should().BeNull();
    }

    [Fact]
    public async Task FailAsync_WhenCompletionMarkerWasPersisted_FinalizesInsteadOfFailing()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        _quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var sut = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System);
        var reservation = await sut.ReserveAsync(fileId, "legacy.bin", 21, default);
        var file = _database.Context.UploadedFiles.Single(x => x.Id == fileId);
        file.Etag = "etag";
        file.Size = 21;
        await _database.Context.SaveChangesAsync();
        await sut.MarkProcessingCompletedAsync(reservation.SessionId, CancellationToken.None);

        await sut.FailAsync(reservation.SessionId, new IOException("lost marker response"), CancellationToken.None);

        var session = _database.Context.UploadSessions.Single(x => x.Id == reservation.SessionId);
        session.Status.Should().Be(UploadSessionStatus.Ready);
        session.ReservedBytes.Should().Be(0);
        session.CleanupPending.Should().BeFalse();
        file.UploadedAt.Should().NotBeNull();
    }

    public void Dispose() => _database.Dispose();
}
