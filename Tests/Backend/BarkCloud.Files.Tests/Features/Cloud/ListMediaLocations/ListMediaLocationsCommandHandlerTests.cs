using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListMediaLocations;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListMediaLocations;

public class ListMediaLocationsCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListMediaLocationsCommandHandler CreateSut() => new(
        _files.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty(),
        NullLogger<ListMediaLocationsCommandHandler>.Instance);

    private static LocatedMediaItem Point(double lat, double lng, DateTime createdAt)
        => new(new UploadFileEntity { Id = Guid.NewGuid(), MediaKind = MediaKind.Photo, CreatedAt = createdAt }, lat, lng, null);

    [Fact]
    public async Task Handle_MapsCoordinates_AndKind()
    {
        _files.Setup(s => s.ListMediaWithLocationPage(OwnerId, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocatedMediaItem> { Point(55.75, 37.61, DateTime.UtcNow) });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListMediaLocationsCommand { Limit = 100 }, default);

        response.Points.Should().ContainSingle();
        response.Points[0].Latitude.Should().Be(55.75);
        response.Points[0].Longitude.Should().Be(37.61);
        response.Points[0].MediaKind.Should().Be(BarkCloud.Proto.Files.MediaKind.Photo);
        response.NextCursorFileId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var now = DateTime.UtcNow;
        // limit=1 → storage возвращает 2 (limit+1), хендлер обрезает до 1 и выставляет курсор.
        _files.Setup(s => s.ListMediaWithLocationPage(OwnerId, null, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocatedMediaItem>
            {
                Point(1, 1, now),
                Point(2, 2, now.AddMinutes(-1))
            });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListMediaLocationsCommand { Limit = 1 }, default);

        response.Points.Should().ContainSingle();
        response.NextCursorFileId.Should().NotBeNullOrEmpty();
        response.NextCursorCreatedAt.Should().NotBeNull();
    }
}
