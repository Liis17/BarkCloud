using BarkCloud.Files.Features.Cloud.RenameFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.RenameFileEntry;

public class RenameFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private RenameFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RenameFileEntryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = Guid.NewGuid(), NewName = " " }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = Guid.NewGuid(), NewName = "x" }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "x" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_SameName_NoUpdate()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, Name = "Same" });

        await CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "Same" }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NameConflict_Throws()
    {
        var id = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = dirId, Name = "Old" });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, dirId, "Taken", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "Taken" }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = dirId, Name = "Old" });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, dirId, "New", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = " New " }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.Is<DomainFileEntry>(e => e.Name == "New"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
