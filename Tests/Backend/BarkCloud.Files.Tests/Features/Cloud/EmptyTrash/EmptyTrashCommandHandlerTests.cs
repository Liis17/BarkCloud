using BarkCloud.Files.Features.Cloud.EmptyTrash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.EmptyTrash;

public class EmptyTrashCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ITrashPurgeService> _purge = new();

    private EmptyTrashCommandHandler CreateSut() => new(
        _storage.Object, _purge.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<EmptyTrashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_PurgesAllTrashedEntries()
    {
        var entries = new List<DomainFileEntry>
        {
            new() { Id = Guid.NewGuid(), OwnerId = OwnerId },
            new() { Id = Guid.NewGuid(), OwnerId = OwnerId },
        };
        _storage.Setup(s => s.GetAllTrashedEntries(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        await CreateSut().Handle(new EmptyTrashCommand(), default);

        _purge.Verify(p => p.PurgeEntriesAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyTrash_StillCallsPurge()
    {
        _storage.Setup(s => s.GetAllTrashedEntries(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<DomainFileEntry>());

        await CreateSut().Handle(new EmptyTrashCommand(), default);

        _purge.Verify(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<DomainFileEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
