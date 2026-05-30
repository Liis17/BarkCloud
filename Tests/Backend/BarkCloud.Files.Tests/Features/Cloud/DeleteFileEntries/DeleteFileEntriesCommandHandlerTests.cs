using BarkCloud.Files.Features.Cloud.DeleteFileEntries;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteFileEntries;

public class DeleteFileEntriesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private DeleteFileEntriesCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteFileEntriesCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyEntryIds_ReturnsZeroAndSkipsStorage()
    {
        var response = await CreateSut().Handle(new DeleteFileEntriesCommand { EntryIds = Array.Empty<Guid>() }, default);

        response.DeletedCount.Should().Be(0);
        _storage.Verify(s => s.GetLiveFileEntriesByIds(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DuplicateIds_QueriesDistinctSet()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _storage.Setup(s => s.GetLiveFileEntriesByIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());

        await CreateSut().Handle(new DeleteFileEntriesCommand { EntryIds = new[] { id1, id1, id2 } }, default);

        _storage.Verify(s => s.GetLiveFileEntriesByIds(
            OwnerId,
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(id1) && ids.Contains(id2)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HappyPath_SoftDeletesOwnerScopedEntriesAndSaves()
    {
        var e1 = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid() };
        var e2 = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid() };
        _storage.Setup(s => s.GetLiveFileEntriesByIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry> { e1, e2 });

        var response = await CreateSut().Handle(new DeleteFileEntriesCommand { EntryIds = new[] { e1.Id, e2.Id } }, default);

        response.DeletedCount.Should().Be(2);
        foreach (var entry in new[] { e1, e2 })
        {
            entry.IsDeleted.Should().BeTrue();
            entry.DeletedAt.Should().NotBeNull();
            entry.PurgeAt.Should().BeCloseTo(entry.DeletedAt!.Value + TrashPurgeService.Retention, TimeSpan.FromSeconds(5));
        }

        // Владелец берётся из контекста — фильтрация чужих записей делегирована storage.
        _storage.Verify(s => s.GetLiveFileEntriesByIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SomeIdsFilteredByStorage_ReturnsMovedCountNotRequested()
    {
        var live = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid() };
        // Запрошено три id, но storage вернул один (остальные чужие/несуществующие/уже удалённые).
        _storage.Setup(s => s.GetLiveFileEntriesByIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry> { live });

        var response = await CreateSut().Handle(
            new DeleteFileEntriesCommand { EntryIds = new[] { live.Id, Guid.NewGuid(), Guid.NewGuid() } }, default);

        response.DeletedCount.Should().Be(1);
        live.IsDeleted.Should().BeTrue();
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
