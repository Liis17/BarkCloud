using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.MoveDirectory;

public class MoveDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private MoveDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<MoveDirectoryCommandHandler>.Instance);

    private void SetupDir(Guid id, CloudDirectory dir) =>
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>())).ReturnsAsync(dir);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_NoOp_SameParent_NoUpdate()
    {
        var id = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = parentId });

        await CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = parentId }, default);

        _storage.Verify(s => s.UpdateDirectory(It.IsAny<CloudDirectory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MoveIntoSelf_ThrowsCircular()
    {
        var id = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = null });

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = id }, default);

        await act.Should().ThrowAsync<CircularMoveException>();
    }

    [Fact]
    public async Task Handle_NewParentNotFound_Throws()
    {
        var id = Guid.NewGuid();
        var newParent = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = null });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newParent, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = newParent }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_CircularViaAncestor_ThrowsCircular()
    {
        var id = Guid.NewGuid();
        var newParent = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = null });
        // newParent's parent chain leads back to id → цикл.
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newParent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newParent, OwnerId = OwnerId, ParentId = id });

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = newParent }, default);

        await act.Should().ThrowAsync<CircularMoveException>();
    }

    [Fact]
    public async Task Handle_NameConflictInNewParent_Throws()
    {
        var id = Guid.NewGuid();
        var newParent = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = null, Name = "Docs" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newParent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newParent, OwnerId = OwnerId, ParentId = null });
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, newParent, "Docs", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = newParent }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        var newParent = Guid.NewGuid();
        SetupDir(id, new CloudDirectory { Id = id, OwnerId = OwnerId, ParentId = null, Name = "Docs" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newParent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newParent, OwnerId = OwnerId, ParentId = null });
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, newParent, "Docs", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new MoveDirectoryCommand { DirectoryId = id, NewParentId = newParent }, default);

        _storage.Verify(s => s.UpdateDirectory(It.Is<CloudDirectory>(d => d.ParentId == newParent), It.IsAny<CancellationToken>()), Times.Once);
    }
}
