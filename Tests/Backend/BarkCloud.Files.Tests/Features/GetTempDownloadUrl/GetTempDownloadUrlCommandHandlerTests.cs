using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetTempDownloadUrl;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetTempDownloadUrl;

public class GetTempDownloadUrlCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ITempFilesStorage> _temp = new();

    private GetTempDownloadUrlCommandHandler CreateSut() => new(
        _files.Object,
        _temp.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty(),
        NullLogger<GetTempDownloadUrlCommandHandler>.Instance);

    [Fact]
    public async Task Handle_FilesNotFound_ThrowsFileNotFound()
    {
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync((List<UploadFileEntity>)null!);

        var act = () => CreateSut().Handle(new GetTempDownloadUrlCommand { FileIds = new List<Guid> { Guid.NewGuid() } }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_CreatesTempLinksAndBuildsUrls()
    {
        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();
        var tempId1 = Guid.NewGuid();
        var tempId2 = Guid.NewGuid();

        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            new() { Id = fileId1 },
            new() { Id = fileId2 }
        });
        _temp.Setup(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>
            {
                new() { Id = tempId1, OriginalFileId = fileId1 },
                new() { Id = tempId2, OriginalFileId = fileId2 }
            });

        var response = await CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { fileId1, fileId2 } }, default);

        response.FileUrls.Should().HaveCount(2);
        response.FileUrls[0].FileId.Should().Be(fileId1.ToString());
        response.FileUrls[0].Url.Should().Contain($"/download/{tempId1}");
        response.FileUrls[1].FileId.Should().Be(fileId2.ToString());
        response.FileUrls[1].Url.Should().Contain($"/download/{tempId2}");
    }
}
