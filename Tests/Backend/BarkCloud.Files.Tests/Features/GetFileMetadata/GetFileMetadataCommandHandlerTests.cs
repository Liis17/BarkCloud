using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFileMetadata;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetFileMetadata;

public class GetFileMetadataCommandHandlerTests
{
    [Fact]
    public async Task Handle_FileIsNotReady_ThrowsFileNotReady()
    {
        const long ownerId = 42;
        var fileId = Guid.NewGuid();
        var files = new Mock<IUploadedFilesStorage>();
        var metadata = new Mock<IFileMetadataStorage>();
        files.Setup(x => x.GetFile(fileId)).ReturnsAsync(new UploadFileEntity
        {
            Id = fileId,
            Uploaders = new List<long> { ownerId },
            UploadedAt = null,
            Etag = null
        });

        var handler = new GetFileMetadataCommandHandler(
            files.Object,
            metadata.Object,
            UserContextFactory.Create(ownerId),
            NullLogger<GetFileMetadataCommandHandler>.Instance);

        var act = () => handler.Handle(new GetFileMetadataCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
        metadata.Verify(x => x.Get(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
