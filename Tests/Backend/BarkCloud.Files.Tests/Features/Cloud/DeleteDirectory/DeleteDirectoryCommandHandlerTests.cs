using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteDirectory;

public class DeleteDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<IFolderShareStorage> _folderShares = new();
    private readonly Mock<IDirectoryGrantStorage> _dirGrants = new();

    private DeleteDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        _folderShares.Object,
        _dirGrants.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteDirectoryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new DeleteDirectoryCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new DeleteDirectoryCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_HappyPath_TrashesFilesAndRemovesDirs()
    {
        var id = Guid.NewGuid();
        var root = new CloudDirectory { Id = id, OwnerId = OwnerId };
        var subtree = new List<CloudDirectory> { root, new() { Id = Guid.NewGuid(), OwnerId = OwnerId, ParentId = id } };
        var entries = new List<CloudFileEntry>
        {
            new() { Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = id, FileId = Guid.NewGuid() }
        };
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        _storage.Setup(s => s.GetSubtree(OwnerId, id, It.IsAny<CancellationToken>())).ReturnsAsync(subtree);
        _storage.Setup(s => s.GetFileEntriesInDirectories(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        await CreateSut().Handle(new DeleteDirectoryCommand { DirectoryId = id }, default);

        entries[0].IsDeleted.Should().BeTrue();
        entries[0].DeletedAt.Should().NotBeNull();
        entries[0].PurgeAt.Should().NotBeNull();
        _storage.Verify(s => s.RemoveDirectories(subtree), Times.Once);
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
