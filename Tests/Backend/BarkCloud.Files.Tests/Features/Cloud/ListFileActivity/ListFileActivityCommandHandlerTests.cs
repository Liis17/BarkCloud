using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListFileActivity;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListFileActivity;

public class ListFileActivityCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IFileActivityStorage> _activity = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    [Fact]
    public async Task Handle_FileStillProcessing_ThrowsFileNotReady()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(x => x.GetFile(fileId)).ReturnsAsync(new UploadFileEntity
        {
            Id = fileId,
            Uploaders = [OwnerId],
            Etag = "etag",
            UploadedAt = null
        });
        var sut = new ListFileActivityCommandHandler(
            _activity.Object,
            _files.Object,
            UserContextFactory.Create(OwnerId));

        var act = () => sut.Handle(new ListFileActivityCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
        _activity.Verify(x => x.ListPage(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
