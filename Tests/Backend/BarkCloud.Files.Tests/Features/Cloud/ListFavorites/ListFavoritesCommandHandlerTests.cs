using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListFavorites;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFavoriteFile = BarkCloud.Files.Domain.FavoriteFile;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListFavorites;

public class ListFavoritesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IFavoriteFilesStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    private ListFavoritesCommandHandler CreateSut() => new(
        _storage.Object, _files.Object, _hierarchy.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListFavoritesCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _storage.Setup(s => s.ListPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFavoriteFile>());

        var response = await CreateSut().Handle(new ListFavoritesCommand { Limit = 50 }, default);

        response.Items.Should().BeEmpty();
        response.NextCursorFileId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SkipsOrphanedAndTrashed_KeepsLive()
    {
        var live = Guid.NewGuid();
        var trashed = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        _storage.Setup(s => s.ListPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFavoriteFile>
            {
                new() { OwnerId = OwnerId, FileId = live, CreatedAt = DateTime.UtcNow },
                new() { OwnerId = OwnerId, FileId = trashed, CreatedAt = DateTime.UtcNow },
                new() { OwnerId = OwnerId, FileId = orphan, CreatedAt = DateTime.UtcNow },
            });
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            new() { Id = live, MediaKind = MediaKind.Photo },
            new() { Id = trashed, MediaKind = MediaKind.Photo },
        });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEffectivelyTrashedFileIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { trashed });

        var response = await CreateSut().Handle(new ListFavoritesCommand { Limit = 50 }, default);

        response.Items.Should().ContainSingle().Which.File.Id.Should().Be(live.ToString());
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        _storage.Setup(s => s.ListPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(id => new DomainFavoriteFile { OwnerId = OwnerId, FileId = id, CreatedAt = DateTime.UtcNow }).ToList());
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(ids.Select(id => new UploadFileEntity { Id = id, MediaKind = MediaKind.Photo }).ToList());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEffectivelyTrashedFileIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());

        var response = await CreateSut().Handle(new ListFavoritesCommand { Limit = 2 }, default);

        response.Items.Should().HaveCount(2);
        response.NextCursorFileId.Should().Be(ids[1].ToString());
    }
}
