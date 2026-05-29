using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFileData;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetFileData;

public class GetFileDataCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetFileDataCommandHandler CreateSut() => new(
        _files.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<GetFileDataCommandHandler>.Instance);

    [Fact]
    public async Task Handle_FileNotFound_Throws()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(new GetFileDataCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ReturnsFileInfo()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Filename = "a.jpg" });
        _files.Setup(s => s.GetPreviewsForFile(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<FilePreview>());

        var response = await CreateSut().Handle(new GetFileDataCommand { FileId = fileId }, default);

        response.FileInfo.Id.Should().Be(fileId.ToString());
    }
}
