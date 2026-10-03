using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SearchFiles;
using BarkCloud.Files.Features.DynamicFolder.ListDynamicFolderItems;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public sealed class FilePlaceholderApiTests
{
    private const long OwnerId = 42;
    private readonly UploadFile[] _page =
    [
        new() { Id = Guid.NewGuid(), MediaKind = MediaKind.Photo, CreatedAt = DateTime.UtcNow },
        new() { Id = Guid.NewGuid(), MediaKind = MediaKind.Video, CreatedAt = DateTime.UtcNow }
    ];
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    public FilePlaceholderApiTests()
    {
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(_page.ToList());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _files.Setup(s => s.GetPlaceholdersForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_page.ToDictionary(f => f.Id, f => new FilePlaceholder
            {
                FileId = f.Id, Colors = Enumerable.Repeat("#112233", 9).ToArray(),
                AspectRatio = f.MediaKind == MediaKind.Photo ? 1 : 16f / 9
            }));
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>());
    }

    [Theory]
    [InlineData(SystemDynamicFolders.KeyRecentMedia)]
    [InlineData(SystemDynamicFolders.KeyDuplicateMedia)]
    public async Task DynamicFolderPage_EmbedsColorsWithOneBatchRead(string folderId)
    {
        var folders = new Mock<IDynamicFolderStorage>();
        folders.Setup(s => s.ListItemsPage(OwnerId, It.IsAny<DynamicFolderCriteria>(), It.IsAny<DateTime>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_page.ToList());
        folders.Setup(s => s.ListDuplicateItemsPage(OwnerId, true, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 50,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_page.Select(f => new DuplicateFileItem(f, "same-hash")).ToList());
        var handler = new ListDynamicFolderItemsCommandHandler(
            folders.Object, _files.Object, _hierarchy.Object, UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
            NullLogger<ListDynamicFolderItemsCommandHandler>.Instance);

        var response = await handler.Handle(new ListDynamicFolderItemsCommand { FolderId = folderId, Limit = 50 }, default);

        AssertPage(response.Items.Select(item => item.File));
        if (folderId == SystemDynamicFolders.KeyDuplicateMedia)
            response.Items.Should().OnlyContain(item => item.DuplicateGroupKey == "same-hash");
    }

    [Fact]
    public async Task LegacySearchPage_EmbedsColorsWithOneBatchRead()
    {
        _hierarchy.Setup(s => s.SearchFileEntriesPage(OwnerId, "photo", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 50,
                It.IsAny<IReadOnlyCollection<MediaKind>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_page.Select(f => new CloudFileEntry
            {
                Id = Guid.NewGuid(), FileId = f.Id, OwnerId = OwnerId, Name = "photo.jpg", CreatedAt = DateTime.UtcNow
            }).ToList());
        var handler = new SearchFilesCommandHandler(_hierarchy.Object, _files.Object,
            UserContextFactory.Create(OwnerId), new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var response = await handler.Handle(new SearchFilesCommand { Query = "photo", Limit = 50 }, default);

        AssertPage(response.Files.Select(entry => entry.File));
    }

    [Fact]
    public void WireContract_PreservesAssignedFieldNumbers()
    {
        BarkCloud.Proto.Files.UploadFileInfo.Descriptor.FindFieldByNumber(19).Name.Should().Be("placeholder");
        BarkCloud.Proto.Files.SearchHit.Descriptor.FindFieldByNumber(15).Name.Should().Be("placeholder");
    }

    private void AssertPage(IEnumerable<BarkCloud.Proto.Files.UploadFileInfo> files)
    {
        var result = files.ToArray();
        result.Should().HaveCount(2);
        result.Should().OnlyContain(file => file.Placeholder.Colors.Count == 9);
        result.Single(f => f.Id == _page[0].Id.ToString()).Placeholder.AspectRatio.Should().Be(1);
        result.Single(f => f.Id == _page[1].Id.ToString()).Placeholder.AspectRatio.Should().Be(16f / 9);
        _files.Verify(s => s.GetPlaceholdersForFiles(
            It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2 && ids.Contains(_page[0].Id) && ids.Contains(_page[1].Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
