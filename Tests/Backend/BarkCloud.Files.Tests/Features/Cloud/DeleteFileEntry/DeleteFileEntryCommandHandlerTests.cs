using BarkCloud.Files.Features.Cloud.DeleteFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteFileEntry;

public class DeleteFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private DeleteFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteFileEntryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_AlreadyDeleted_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, IsDeleted = true });

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_HappyPath_SoftDeletesAndSaves()
    {
        var id = Guid.NewGuid();
        var entry = new DomainFileEntry { Id = id, OwnerId = OwnerId, FileId = Guid.NewGuid() };
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);

        await CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        entry.IsDeleted.Should().BeTrue();
        entry.DeletedAt.Should().NotBeNull();
        entry.PurgeAt.Should().NotBeNull();
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
