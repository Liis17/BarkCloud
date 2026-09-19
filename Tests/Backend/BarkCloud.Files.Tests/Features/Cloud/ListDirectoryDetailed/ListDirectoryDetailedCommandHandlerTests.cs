using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListDirectoryDetailed;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListDirectoryDetailed;

public class ListDirectoryDetailedCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListDirectoryDetailedCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListDirectoryDetailedCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DirNotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new ListDirectoryDetailedCommand { DirectoryId = id }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_Root_HidesEntriesWithoutReadyFile()
    {
        var liveFile = Guid.NewGuid();
        var orphanFile = Guid.NewGuid();
        _storage.Setup(s => s.ListSubdirectories(OwnerId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory>());
        _storage.Setup(s => s.ListFilesInDirectoryPage(
                OwnerId, CloudHierarchyStorage.RootDirectoryId, null, null, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = liveFile, Name = "live.jpg" },
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = orphanFile, Name = "orphan.jpg" },
            });
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<UploadFileEntity> { new() { Id = liveFile, MediaKind = MediaKind.Photo } });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListDirectoryDetailedCommand { DirectoryId = null }, default);

        response.Files.Should().ContainSingle();
        response.Files[0].File.Id.Should().Be(liveFile.ToString());
    }

    [Fact]
    public async Task Handle_ReturnsCursorWhenStorageHasAnotherPage()
    {
        var first = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Name = "a.txt"
        };
        var second = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Name = "b.txt"
        };
        _storage.Setup(s => s.ListSubdirectories(OwnerId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _storage.Setup(s => s.ListFilesInDirectoryPage(
                OwnerId, CloudHierarchyStorage.RootDirectoryId, null, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([first, second]);
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync([new UploadFileEntity { Id = first.FileId, MediaKind = MediaKind.Other }]);
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListDirectoryDetailedCommand { Limit = 1 }, default);

        response.Files.Should().ContainSingle();
        response.Files[0].Entry.Name.Should().Be("a.txt");
        response.NextCursorName.Should().Be("a.txt");
        response.NextCursorEntryId.Should().Be(first.Id.ToString());
    }
}
