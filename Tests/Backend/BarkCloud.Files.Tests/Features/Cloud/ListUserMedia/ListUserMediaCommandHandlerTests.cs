using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListUserMedia;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListUserMedia;

public class ListUserMediaCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<IFileMetadataStorage> _metadata = new();

    private ListUserMediaCommandHandler CreateSut() => new(
        _files.Object, _hierarchy.Object, _metadata.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListUserMediaCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _files.Setup(s => s.ListUserMediaPage(OwnerId, It.IsAny<MediaKind>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UploadFileEntity>());

        var response = await CreateSut().Handle(new ListUserMediaCommand { Kind = MediaKind.Photo, Limit = 50 }, default);

        response.Items.Should().BeEmpty();
        response.NextCursorFileId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HappyPath_IncludesEntryMetadata()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.ListUserMediaPage(OwnerId, MediaKind.Photo, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UploadFileEntity> { new() { Id = fileId, MediaKind = MediaKind.Photo } });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "a.jpg", CreatedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Name = "b.jpg", CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
            });

        var response = await CreateSut().Handle(new ListUserMediaCommand { Kind = MediaKind.Photo, Limit = 50 }, default);

        var item = response.Items.Should().ContainSingle().Subject;
        item.File.Id.Should().Be(fileId.ToString());
        item.EntriesCount.Should().Be(2);
        item.EntryNames.Should().HaveCount(2);
        item.EntryIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_Video_IncludesVideoMeta()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.ListUserMediaPage(OwnerId, MediaKind.Video, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UploadFileEntity> { new() { Id = fileId, MediaKind = MediaKind.Video } });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());
        _metadata.Setup(s => s.GetForFiles(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, FileMetadata>
            {
                [fileId] = new FileMetadata
                {
                    FileId = fileId, DurationSeconds = 12.5, VideoCodec = "hevc",
                    AudioCodec = "aac", Bitrate = 120_000_000, IsHdr = true,
                },
            });

        var response = await CreateSut().Handle(new ListUserMediaCommand { Kind = MediaKind.Video, Limit = 50 }, default);

        var meta = response.Items.Should().ContainSingle().Subject.File.VideoMeta;
        meta.Should().NotBeNull();
        meta!.DurationSeconds.Should().Be(12.5);
        meta.VideoCodec.Should().Be("hevc");
        meta.AudioCodec.Should().Be("aac");
        meta.Bitrate.Should().Be(120_000_000);
        meta.Hdr.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var files = Enumerable.Range(0, 3)
            .Select(_ => new UploadFileEntity { Id = Guid.NewGuid(), MediaKind = MediaKind.Photo, CreatedAt = DateTime.UtcNow })
            .ToList();
        _files.Setup(s => s.ListUserMediaPage(OwnerId, MediaKind.Photo, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());

        var response = await CreateSut().Handle(new ListUserMediaCommand { Kind = MediaKind.Photo, Limit = 2 }, default);

        response.Items.Should().HaveCount(2);
        response.NextCursorFileId.Should().Be(files[1].Id.ToString());
    }
}
