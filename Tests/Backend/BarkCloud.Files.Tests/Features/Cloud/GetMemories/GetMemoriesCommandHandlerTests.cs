using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.GetMemories;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.GetMemories;

public class GetMemoriesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetMemoriesCommandHandler CreateSut() => new(
        _files.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty(),
        NullLogger<GetMemoriesCommandHandler>.Instance);

    private static MemoryMediaItem Item(int year)
        => new(new UploadFileEntity { Id = Guid.NewGuid(), MediaKind = MediaKind.Photo }, new DateTime(year, 6, 4, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Handle_GroupsByYear_NewestFirst_WithYearsAgoAndTotals()
    {
        _files.Setup(s => s.ListMemoriesForDay(OwnerId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryMediaItem> { Item(2024), Item(2024), Item(2022) });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new GetMemoriesCommand { Month = 6, Day = 4 }, default);

        response.Groups.Should().HaveCount(2);
        response.Groups[0].Year.Should().Be(2024);
        response.Groups[0].TotalCount.Should().Be(2);
        response.Groups[0].YearsAgo.Should().Be(DateTime.UtcNow.Year - 2024);
        response.Groups[1].Year.Should().Be(2022);
        response.Groups[1].Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_CapsItemsPerYear_ButTotalCountReflectsAll()
    {
        var many = Enumerable.Range(0, 5).Select(_ => Item(2020)).ToList();
        _files.Setup(s => s.ListMemoriesForDay(OwnerId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(many);
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new GetMemoriesCommand { Month = 6, Day = 4, PerYearLimit = 2 }, default);

        response.Groups.Should().ContainSingle();
        response.Groups[0].Items.Should().HaveCount(2);
        response.Groups[0].TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task Handle_NoMatches_ReturnsEmpty()
    {
        _files.Setup(s => s.ListMemoriesForDay(OwnerId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryMediaItem>());

        var response = await CreateSut().Handle(new GetMemoriesCommand(), default);

        response.Groups.Should().BeEmpty();
    }
}
