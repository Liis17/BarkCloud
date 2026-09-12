using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.AttachFile;

public class AttachFileCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly MetricsCollector _metrics = new();

    private AttachFileCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<AttachFileCommandHandler>.Instance,
        metrics: _metrics);

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = Guid.NewGuid(), Name = "  " }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_DirectoryNotFound_Throws()
    {
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = Guid.NewGuid(), Name = "f", DirectoryId = dirId }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_FileNotFound_Throws()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = Guid.NewGuid(), Name = "f" }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId, 999));

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_FileStillProcessing_ThrowsFileNotReady()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity
        {
            Id = fileId,
            Uploaders = [OwnerId],
            Etag = "etag",
            UploadedAt = null
        });

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
        _storage.Verify(s => s.AddFileEntry(It.IsAny<CloudFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyAttached_Throws()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        await act.Should().ThrowAsync<FileAlreadyAttachedException>();
    }

    [Fact]
    public async Task Handle_NameConflict_AutoRenamesWithSuffix()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        // Имя "f" занято; "f (1)" свободно (по умолчанию false) → авто-переименование вместо ошибки.
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, "f", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        _storage.Verify(s => s.AddFileEntry(
            It.Is<CloudFileEntry>(e => e.FileId == fileId && e.Name == "f (1)"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsEntryToRoot()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "  photo.jpg  " }, default);

        _storage.Verify(s => s.AddFileEntry(
            It.Is<CloudFileEntry>(e => e.OwnerId == OwnerId && e.FileId == fileId && e.Name == "photo.jpg" && e.DirectoryId == CloudHierarchyStorage.RootDirectoryId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UploadAttachRetry_RecordsMetric()
    {
        var fileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await CreateSut().Handle(new AttachFileCommand
        {
            FileId = fileId,
            Name = "f",
            UploadSessionId = sessionId,
            IsUploadRetry = true
        }, default);

        _metrics.SnapshotAndReset()["upload_attach_retries_total"].Should().Be(1);
    }

    [Theory]
    [InlineData(MediaKind.Photo, CloudDirectorySystemKind.Photos, "Фото")]
    [InlineData(MediaKind.Video, CloudDirectorySystemKind.Videos, "Видео")]
    [InlineData(MediaKind.Document, CloudDirectorySystemKind.OtherDocuments, "Другие документы")]
    [InlineData(MediaKind.Audio, CloudDirectorySystemKind.OtherDocuments, "Другие документы")]
    [InlineData(MediaKind.Other, CloudDirectorySystemKind.OtherDocuments, "Другие документы")]
    public async Task Handle_RouteByMediaKind_RoutesToSystemFolderByType(
        MediaKind kind, CloudDirectorySystemKind expectedSystemKind, string expectedName)
    {
        var fileId = Guid.NewGuid();
        var systemDir = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId, mediaKind: kind));
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _storage.Setup(s => s.EnsureSystemDirectory(OwnerId, expectedSystemKind, expectedName, It.IsAny<CancellationToken>())).ReturnsAsync(systemDir);

        // directory_id присылается, но при route_by_media_kind игнорируется.
        await CreateSut().Handle(
            new AttachFileCommand { FileId = fileId, Name = "f", DirectoryId = Guid.NewGuid(), RouteByMediaKind = true }, default);

        _storage.Verify(s => s.AddFileEntry(
            It.Is<CloudFileEntry>(e => e.FileId == fileId && e.DirectoryId == systemDir),
            It.IsAny<CancellationToken>()), Times.Once);
        // Явный directory_id не валидируется при авто-распределении.
        _storage.Verify(s => s.GetDirectoryAsNoTracking(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UploadFileEntity ReadyFile(
        Guid id,
        long ownerId = OwnerId,
        MediaKind mediaKind = MediaKind.Other) => new()
    {
        Id = id,
        Uploaders = [ownerId],
        MediaKind = mediaKind,
        UploadedAt = DateTime.UtcNow,
        Etag = "etag"
    };
}
