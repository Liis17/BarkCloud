using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.GetPath;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.GetPath;

public class GetPathCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private GetPathCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<GetPathCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Root_ReturnsSlash()
    {
        var response = await CreateSut().Handle(new GetPathCommand(), default);

        response.FullPath.Should().Be("/");
        response.Segments.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EntryNotFound_Throws()
    {
        var entryId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(entryId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudFileEntry?)null);

        var act = () => CreateSut().Handle(new GetPathCommand { EntryId = entryId }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
    }

    [Fact]
    public async Task Handle_DirNotFound_Throws()
    {
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new GetPathCommand { DirectoryId = dirId }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_DirWithAncestors_BuildsFullPath()
    {
        var rootDir = Guid.NewGuid();
        var childDir = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(childDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = childDir, OwnerId = OwnerId, ParentId = rootDir, Name = "Child" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(rootDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = rootDir, OwnerId = OwnerId, ParentId = null, Name = "Root" });

        var response = await CreateSut().Handle(new GetPathCommand { DirectoryId = childDir }, default);

        response.Segments.Select(s => s.Name).Should().Equal("Root");
        response.FullPath.Should().Be("/Root/Child");
    }

    [Fact]
    public async Task Handle_CyclicAncestors_ThrowsTreeCorrupted()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var leaf = Guid.NewGuid();
        // leaf висит на цикле a ↔ b.
        var dirs = new Dictionary<Guid, CloudDirectory>
        {
            [leaf] = new() { Id = leaf, OwnerId = OwnerId, ParentId = a, Name = "Leaf" },
            [a] = new() { Id = a, OwnerId = OwnerId, ParentId = b, Name = "A" },
            [b] = new() { Id = b, OwnerId = OwnerId, ParentId = a, Name = "B" },
        };
        var calls = 0;
        // Моки синхронны, поэтому зависание ловим счётчиком, а не таймаутом.
        _storage.Setup(s => s.GetDirectoryAsNoTracking(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                ++calls > 100 ? throw new InvalidOperationException("Бесконечный обход") : dirs[id]);

        var act = () => CreateSut().Handle(new GetPathCommand { DirectoryId = leaf }, default);

        await act.Should().ThrowAsync<DirectoryTreeCorruptedException>();
    }
}
