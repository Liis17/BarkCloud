using BarkCloud.Files.Features.Cloud.DeleteUserMedia;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;
using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;
using DomainUploadFile = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteUserMedia;

public class DeleteUserMediaCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private DeleteUserMediaCommandHandler CreateSut() => new(
        _hierarchy.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteUserMediaCommandHandler>.Instance);

    [Fact]
    public async Task Handle_HasLiveEntries_SoftDeletesThem()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        var entries = new List<DomainFileEntry>
        {
            new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId },
        };
        _hierarchy.Setup(s => s.GetLiveEntriesForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        await CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        entries[0].IsDeleted.Should().BeTrue();
        entries[0].DeletedAt.Should().NotBeNull();
        entries[0].PurgeAt.Should().NotBeNull();
        _hierarchy.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _files.Verify(s => s.RemoveUploaderFromFile(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoEntries_CreatesTrashedEntry()
    {
        var fileId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetLiveEntriesForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<DomainFileEntry>());
        _hierarchy.Setup(s => s.GetEntriesForFiles(OwnerId, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(fileId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());
        _hierarchy.Setup(s => s.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Videos, "Видео", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(
            fileId, "clip.mp4", DomainMediaKind.Video));

        await CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        _hierarchy.Verify(s => s.AddFileEntry(
            It.Is<DomainFileEntry>(e =>
                e.OwnerId == OwnerId
                && e.FileId == fileId
                && e.Name == "clip.mp4"
                && e.IsDeleted
                && e.DeletedAt != null
                && e.PurgeAt != null),
            It.IsAny<CancellationToken>()), Times.Once);
        _files.Verify(s => s.RemoveUploaderFromFile(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _hierarchy.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyTrashedEntry_DoesNothing()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(ReadyFile(fileId));
        _hierarchy.Setup(s => s.GetLiveEntriesForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<DomainFileEntry>());
        _hierarchy.Setup(s => s.GetEntriesForFiles(OwnerId, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(fileId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry> { new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, IsDeleted = true } });

        await CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        _hierarchy.Verify(s => s.AddFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        _files.Verify(s => s.RemoveUploaderFromFile(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FileStillProcessing_ThrowsFileNotReady()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new DomainUploadFile
        {
            Id = fileId,
            Uploaders = [OwnerId],
            Etag = "etag"
        });

        var act = () => CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
        _hierarchy.Verify(s => s.GetLiveEntriesForFile(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DomainUploadFile ReadyFile(
        Guid id,
        string fileName = "file.bin",
        DomainMediaKind mediaKind = DomainMediaKind.Other) => new()
    {
        Id = id,
        Filename = fileName,
        MediaKind = mediaKind,
        Uploaders = [OwnerId],
        UploadedAt = DateTime.UtcNow,
        Etag = "etag"
    };
}
