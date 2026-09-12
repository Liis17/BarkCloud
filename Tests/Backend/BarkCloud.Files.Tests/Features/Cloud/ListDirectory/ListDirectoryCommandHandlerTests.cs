using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListDirectory;

public class ListDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        _files.Object,
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
        var fileId = Guid.NewGuid();
        _storage.Setup(s => s.ListFilesInDirectory(OwnerId, CloudHierarchyStorage.RootDirectoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry> { new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "f.jpg" } });
        _files.Setup(s => s.GetFiles(It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { fileId }))))
            .ReturnsAsync([ReadyFile(fileId)]);

        var response = await CreateSut().Handle(new ListDirectoryCommand { DirectoryId = null }, default);

        response.Subdirs.Should().ContainSingle().Which.Name.Should().Be("Sub");
        response.Files.Should().ContainSingle().Which.Name.Should().Be("f.jpg");
    }

    [Fact]
    public async Task Handle_Root_HidesEntriesWhoseFileIsNotReady()
    {
        var fileId = Guid.NewGuid();
        _storage.Setup(s => s.ListSubdirectories(OwnerId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _storage.Setup(s => s.ListFilesInDirectory(OwnerId, CloudHierarchyStorage.RootDirectoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CloudFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "processing.bin" }]);
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync([]);

        var response = await CreateSut().Handle(new ListDirectoryCommand(), default);

        response.Files.Should().BeEmpty();
    }

    private static UploadFileEntity ReadyFile(Guid id) => new()
    {
        Id = id,
        UploadedAt = DateTime.UtcNow,
        Etag = "etag"
    };
}
