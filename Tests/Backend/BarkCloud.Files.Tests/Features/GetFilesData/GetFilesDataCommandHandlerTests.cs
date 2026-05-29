using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFilesData;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetFilesData;

public class GetFilesDataCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetFilesDataCommandHandler CreateSut() => new(
        _files.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<GetFilesDataCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new GetFilesDataCommand { FileIds = new List<Guid>() }, default);

        response.FilesInfos.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HappyPath_MapsAll()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<UploadFileEntity>
            {
                new() { Id = first, Filename = "a.jpg" },
                new() { Id = second, Filename = "b.jpg" },
            });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new GetFilesDataCommand { FileIds = new List<Guid> { first, second } }, default);

        response.FilesInfos.Should().HaveCount(2);
        response.FilesInfos.Select(f => f.Id).Should().BeEquivalentTo(new[] { first.ToString(), second.ToString() });
    }
}
