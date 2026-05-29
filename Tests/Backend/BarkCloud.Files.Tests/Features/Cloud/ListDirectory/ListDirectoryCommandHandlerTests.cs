using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.ListDirectory;

public class ListDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private ListDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<ListDirectoryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DirNotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new ListDirectoryCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_DirNotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new ListDirectoryCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_Root_ListsSubdirsAndFiles()
    {
        _storage.Setup(s => s.ListSubdirectories(OwnerId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory> { new() { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "Sub" } });
        _storage.Setup(s => s.ListFilesInDirectory(OwnerId, CloudHierarchyStorage.RootDirectoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry> { new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Name = "f.jpg" } });

        var response = await CreateSut().Handle(new ListDirectoryCommand { DirectoryId = null }, default);

        response.Subdirs.Should().ContainSingle().Which.Name.Should().Be("Sub");
        response.Files.Should().ContainSingle().Which.Name.Should().Be("f.jpg");
    }
}
