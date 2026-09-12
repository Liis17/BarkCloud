using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SearchFiles;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.SearchFiles;

public class SearchFilesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    [Fact]
    public async Task Handle_FileIsNotReady_HidesSearchEntry()
    {
        var fileId = Guid.NewGuid();
        _hierarchy.Setup(x => x.SearchFileEntriesPage(
                OwnerId, "draft", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 50,
                It.IsAny<IReadOnlyCollection<MediaKind>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "draft.jpg" }
            });
        _files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<UploadFileEntity>());
        _files.Setup(x => x.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var handler = new SearchFilesCommandHandler(
            _hierarchy.Object,
            _files.Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var response = await handler.Handle(new SearchFilesCommand { Query = "draft", Limit = 50 }, default);

        response.Files.Should().BeEmpty();
    }
}
