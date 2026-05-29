using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.RenameDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.RenameDirectory;

public class RenameDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private RenameDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RenameDirectoryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = Guid.NewGuid(), NewName = " " }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = "x" }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = "x" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_SameName_NoUpdate()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = OwnerId, Name = "Same" });

        await CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = "Same" }, default);

        _storage.Verify(s => s.UpdateDirectory(It.IsAny<CloudDirectory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NameExists_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = OwnerId, Name = "Old", ParentId = null });
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, null, "Taken", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = "Taken" }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = OwnerId, Name = "Old" });
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, It.IsAny<Guid?>(), "New", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = " New " }, default);

        _storage.Verify(s => s.UpdateDirectory(It.Is<CloudDirectory>(d => d.Name == "New"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
