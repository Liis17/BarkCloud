using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.RestoreFromTrash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.RestoreFromTrash;

public class RestoreFromTrashCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private RestoreFromTrashCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RestoreFromTrashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetTrashedEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new RestoreFromTrashCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new RestoreFromTrashCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_LiveEntryAlreadyExists_ThrowsAlreadyAttached()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, FileId = fileId });
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new RestoreFromTrashCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<FileAlreadyAttachedException>();
    }

    [Fact]
    public async Task Handle_SourceDirGone_RestoresToRootAndClearsTrashFlags()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var deadDir = Guid.NewGuid();
        var entry = new DomainFileEntry
        {
            Id = id, OwnerId = OwnerId, FileId = fileId, DirectoryId = deadDir,
            Name = "photo.jpg", IsDeleted = true, DeletedAt = DateTime.UtcNow, PurgeAt = DateTime.UtcNow
        };
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        // Исходная папка удалена → null.
        _storage.Setup(s => s.GetDirectoryAsNoTracking(deadDir, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, "photo.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new RestoreFromTrashCommand { EntryId = id }, default);

        entry.IsDeleted.Should().BeFalse();
        entry.DeletedAt.Should().BeNull();
        entry.PurgeAt.Should().BeNull();
        entry.DirectoryId.Should().Be(CloudHierarchyStorage.RootDirectoryId);
        entry.Name.Should().Be("photo.jpg");
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NameConflict_AppendsSuffix()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var entry = new DomainFileEntry
        {
            Id = id, OwnerId = OwnerId, FileId = fileId,
            DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "photo.jpg", IsDeleted = true
        };
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _storage.Setup(s => s.FileEntryExistsForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, "photo.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, CloudHierarchyStorage.RootDirectoryId, "photo (1).jpg", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new RestoreFromTrashCommand { EntryId = id }, default);

        entry.Name.Should().Be("photo (1).jpg");
    }
}
