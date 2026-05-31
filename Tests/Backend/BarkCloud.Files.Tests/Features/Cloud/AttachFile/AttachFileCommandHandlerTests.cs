using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
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

    private AttachFileCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<AttachFileCommandHandler>.Instance);

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
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { 999 } });

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_AlreadyAttached_Throws()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "f" }, default);

        await act.Should().ThrowAsync<FileAlreadyAttachedException>();
    }

    [Fact]
    public async Task Handle_NameConflict_AutoRenamesWithSuffix()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
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
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new AttachFileCommand { FileId = fileId, Name = "  photo.jpg  " }, default);

        _storage.Verify(s => s.AddFileEntry(
            It.Is<CloudFileEntry>(e => e.OwnerId == OwnerId && e.FileId == fileId && e.Name == "photo.jpg" && e.DirectoryId == CloudHierarchyStorage.RootDirectoryId),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
