using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.MoveFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.MoveFileEntry;

public class MoveFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private MoveFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<MoveFileEntryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_NewDirectoryNotFound_Throws()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_SameDirectory_NoUpdate()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId });

        await CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = null }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NameConflictInTarget_Throws()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "f.jpg" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newDir, OwnerId = OwnerId });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, newDir, "f.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "f.jpg" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newDir, OwnerId = OwnerId });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, newDir, "f.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.Is<DomainFileEntry>(e => e.DirectoryId == newDir), It.IsAny<CancellationToken>()), Times.Once);
    }
}
