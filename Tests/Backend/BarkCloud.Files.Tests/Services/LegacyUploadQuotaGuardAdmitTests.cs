using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

namespace BarkCloud.Files.Tests.Services;

public class LegacyUploadQuotaGuardAdmitTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageQuotaService> _quota = new();
    private readonly LegacyUploadOptions _options = new();

    private LegacyUploadQuotaGuard CreateSut() =>
        new(_database.Context, _quota.Object, TimeProvider.System, _options);

    private async Task<Guid> AddFileAsync(UploadFileType type = UploadFileType.CloudFile, bool ready = false)
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            UploadedAt = ready ? DateTime.UtcNow : null,
            Etag = ready ? "etag" : null,
            Type = type,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        return fileId;
    }

    private async Task AddSessionAsync(
        Guid fileId,
        UploadSessionStatus status,
        long declaredSize = 21,
        bool markerSet = false)
    {
        var now = DateTime.UtcNow;
        _database.Context.UploadSessions.Add(new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = fileId,
            OwnerId = 42,
            IdempotencyKey = $"legacy:{fileId:N}",
            FileName = "legacy.bin",
            DeclaredSize = declaredSize,
            ContentType = "application/octet-stream",
            Sha256 = string.Empty,
            Status = status,
            StorageProfileId = "universal-v1",
            MultipartUploadId = string.Empty,
            PartSize = declaredSize,
            UploadTokenHash = string.Empty,
            ReservedBytes = declaredSize,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.AddHours(24),
            LegacyProcessingCompletedAt = markerSet ? now : null,
            ConcurrencyToken = Guid.NewGuid()
        });
        await _database.Context.SaveChangesAsync();
    }

    private void SetupQuota(long? limit, long used = 0, long reserved = 0) =>
        _quota.Setup(x => x.GetSnapshotAsync(42, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(limit, used, reserved));

    [Fact]
    public async Task AdmitAsync_WhenFileIsUnknown_ThrowsFileNotFound()
    {
        var act = () => CreateSut().AdmitAsync(Guid.NewGuid(), 1024, default);

        await act.Should().ThrowAsync<BarkCloud.Shared.Exceptions.Files.FileNotFoundException>();
        _quota.Verify(
            x => x.GetSnapshotAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_WhenFileIsAlreadyReady_ThrowsAlreadyUploaded()
    {
        var fileId = await AddFileAsync(ready: true);

        var act = () => CreateSut().AdmitAsync(fileId, 1024, default);

        await act.Should().ThrowAsync<FileAlreadyUploadedException>();
    }

    [Fact]
    public async Task AdmitAsync_WhenLegacyProcessingIsActiveWithoutMarker_Rejects()
    {
        var fileId = await AddFileAsync();
        await AddSessionAsync(fileId, UploadSessionStatus.Processing);

        var act = () => CreateSut().AdmitAsync(fileId, 1024, default);

        (await act.Should().ThrowAsync<FileAlreadyUploadedException>())
            .WithMessage("*уже выполняется*");
    }

    [Fact]
    public async Task AdmitAsync_WhenSessionFailed_Rejects()
    {
        var fileId = await AddFileAsync();
        await AddSessionAsync(fileId, UploadSessionStatus.Failed);

        var act = () => CreateSut().AdmitAsync(fileId, 1024, default);

        (await act.Should().ThrowAsync<FileAlreadyUploadedException>())
            .WithMessage("*уже завершён*");
    }

    [Fact]
    public async Task AdmitAsync_WhenReplayWithMarker_CapsBodyAtDeclaredSizeWithoutQuotaCall()
    {
        var fileId = await AddFileAsync();
        await AddSessionAsync(fileId, UploadSessionStatus.Processing, declaredSize: 777, markerSet: true);

        var admission = await CreateSut().AdmitAsync(fileId, 1024, default);

        admission.MaxBytes.Should().Be(777);
        _quota.Verify(
            x => x.GetSnapshotAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_WhenQuotaIsUnlimited_UsesMaxFileBytes()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: null);

        var admission = await CreateSut().AdmitAsync(fileId, 5 * Gib, default);

        admission.MaxBytes.Should().Be(10 * Gib);
        admission.OwnerId.Should().Be(42);
    }

    [Fact]
    public async Task AdmitAsync_WhenQuotaRemainderIsSmaller_UsesRemainder()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: 100 * 1024 * 1024, used: 60 * 1024 * 1024, reserved: 10 * 1024 * 1024);

        var admission = await CreateSut().AdmitAsync(fileId, 1024 * 1024, default);

        admission.MaxBytes.Should().Be(30 * 1024 * 1024);
    }

    [Fact]
    public async Task AdmitAsync_WhenContentLengthExceedsQuotaRemainder_ThrowsQuotaExceeded()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: 100, used: 80);
        _options.MultipartOverheadBytes = 10;

        var act = () => CreateSut().AdmitAsync(fileId, 21 + 10, default);

        var exception = (await act.Should().ThrowAsync<UploadQuotaExceededException>()).Which;
        exception.LimitBytes.Should().Be(100);
        exception.UsedBytes.Should().Be(80);
        exception.RequestedBytes.Should().Be(21);
    }

    [Fact]
    public async Task AdmitAsync_WhenContentLengthFitsQuotaRemainderPlusOverhead_Admits()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: 100, used: 80);
        _options.MultipartOverheadBytes = 10;

        var admission = await CreateSut().AdmitAsync(fileId, 20 + 10, default);

        admission.MaxBytes.Should().Be(20);
    }

    [Fact]
    public async Task AdmitAsync_WhenQuotaIsExhausted_ThrowsEvenWithoutContentLength()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: 100, used: 90, reserved: 10);

        var act = () => CreateSut().AdmitAsync(fileId, null, default);

        await act.Should().ThrowAsync<UploadQuotaExceededException>();
    }

    [Fact]
    public async Task AdmitAsync_ForAvatar_UsesAvatarCap()
    {
        var fileId = await AddFileAsync(UploadFileType.UserAvatar);
        SetupQuota(limit: null);

        var admission = await CreateSut().AdmitAsync(fileId, 1024, default);

        admission.MaxBytes.Should().Be(_options.MaxAvatarBytes);
        admission.FileType.Should().Be(UploadFileType.UserAvatar);
    }

    [Fact]
    public async Task AdmitAsync_WhenBufferBudgetIsBelowMaxFileBytes_CapsAtBudget()
    {
        var fileId = await AddFileAsync();
        SetupQuota(limit: null);
        _options.MaxBufferedBytes = 2 * Gib;

        var admission = await CreateSut().AdmitAsync(fileId, 1024, default);

        admission.MaxBytes.Should().Be(2 * Gib);
    }

    public void Dispose() => _database.Dispose();
}
