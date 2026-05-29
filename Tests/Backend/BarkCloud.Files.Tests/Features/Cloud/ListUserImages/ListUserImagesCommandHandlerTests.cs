using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListUserImages;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListUserImages;

public class ListUserImagesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    private ListUserImagesCommandHandler CreateSut() => new(
        _files.Object, _hierarchy.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListUserImagesCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _files.Setup(s => s.ListUserImagesPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UploadFileEntity>());

        var response = await CreateSut().Handle(new ListUserImagesCommand { Limit = 50 }, default);

        response.Items.Should().BeEmpty();
        response.NextCursorFileId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HappyPath_IncludesEntryMetadata()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.ListUserImagesPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UploadFileEntity> { new() { Id = fileId, MediaKind = MediaKind.Photo } });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "a.jpg", CreatedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "b.jpg", CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
            });

        var response = await CreateSut().Handle(new ListUserImagesCommand { Limit = 50 }, default);

        var item = response.Items.Should().ContainSingle().Subject;
        item.File.Id.Should().Be(fileId.ToString());
        item.EntriesCount.Should().Be(2);
        item.EntryNames.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var files = Enumerable.Range(0, 3)
            .Select(_ => new UploadFileEntity { Id = Guid.NewGuid(), MediaKind = MediaKind.Photo, CreatedAt = DateTime.UtcNow })
            .ToList();
        _files.Setup(s => s.ListUserImagesPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());

        var response = await CreateSut().Handle(new ListUserImagesCommand { Limit = 2 }, default);

        response.Items.Should().HaveCount(2);
        response.NextCursorFileId.Should().Be(files[1].Id.ToString());
    }
}
