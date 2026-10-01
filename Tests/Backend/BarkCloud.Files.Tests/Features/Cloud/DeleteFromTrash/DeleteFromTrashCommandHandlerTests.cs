using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.DeleteFromTrash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteFromTrash;

public class DeleteFromTrashCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ITrashPurgeService> _purge = new();

    private DeleteFromTrashCommandHandler CreateSut() => new(
        _storage.Object, _purge.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteFromTrashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetTrashedEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new DeleteFromTrashCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        _purge.Verify(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<DomainFileEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new DeleteFromTrashCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _purge.Verify(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<DomainFileEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_PurgesEntry()
    {
        var id = Guid.NewGuid();
        var entry = new DomainFileEntry { Id = id, OwnerId = OwnerId };
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _purge.Setup(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<DomainFileEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrashPurgeResult(1, 1));

        await CreateSut().Handle(new DeleteFromTrashCommand { EntryId = id }, default);

        _purge.Verify(p => p.PurgeEntriesAsync(
            It.Is<IReadOnlyCollection<DomainFileEntry>>(c => c.Single() == entry),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_EntryRestoredConcurrently_ThrowsNotFoundAndWritesNoActivity()
    {
        // Между выбором записи и purge её восстановили: purge ничего не удалил.
        var id = Guid.NewGuid();
        var entry = new DomainFileEntry { Id = id, OwnerId = OwnerId };
        _storage.Setup(s => s.GetTrashedEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _purge.Setup(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<DomainFileEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrashPurgeResult(0, 0));
        var activityStorage = new Mock<IFileActivityStorage>();
        var sut = new DeleteFromTrashCommandHandler(
            _storage.Object, _purge.Object,
            UserContextFactory.Create(OwnerId),
            NullLogger<DeleteFromTrashCommandHandler>.Instance,
            new FileActivityWriter(activityStorage.Object, NullLogger<FileActivityWriter>.Instance));

        var act = () => sut.Handle(new DeleteFromTrashCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        activityStorage.Verify(
            s => s.AddRange(It.IsAny<IEnumerable<FileActivityEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
