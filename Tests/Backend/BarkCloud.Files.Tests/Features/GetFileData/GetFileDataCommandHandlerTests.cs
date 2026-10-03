using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFileData;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Features.GetFileData;

public class GetFileDataCommandHandlerTests
{
    private const long OwnerId = 42;
    private const long OtherUserId = 7;

    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetFileDataCommandHandler CreateSut() => new(
        _files.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<GetFileDataCommandHandler>.Instance);

    private void SetupFile(UploadFileEntity file)
    {
        _files.Setup(s => s.GetFile(file.Id)).ReturnsAsync(file);
        _files.Setup(s => s.GetPreviewsForFile(file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<FilePreview>());
        _files.Setup(s => s.GetPlaceholdersForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, FilePlaceholder>());
    }

    private static UploadFileEntity ReadyFile(Guid id, UploadFileType type, params long[] uploaders) => new()
    {
        Id = id,
        Filename = "a.jpg",
        Etag = "etag",
        UploadedAt = DateTime.UtcNow,
        Type = type,
        Uploaders = uploaders.ToList()
    };

    [Fact]
    public async Task Handle_FileNotFound_Throws()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(new GetFileDataCommand { FileId = Guid.NewGuid(), UserId = OwnerId }, default);

        await act.Should().ThrowAsync<System.IO.FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_OwnCloudFile_ReturnsFileInfo()
    {
        var fileId = Guid.NewGuid();
        SetupFile(ReadyFile(fileId, UploadFileType.CloudFile, OwnerId));
        _files.Setup(s => s.GetPlaceholdersForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, FilePlaceholder>
            {
                [fileId] = new() { FileId = fileId, Colors = Enumerable.Repeat("#112233", 9).ToArray(), AspectRatio = 1 }
            });

        var response = await CreateSut().Handle(new GetFileDataCommand { FileId = fileId, UserId = OwnerId }, default);

        response.FileInfo.Id.Should().Be(fileId.ToString());
        response.FileInfo.Placeholder.Colors.Should().Equal(Enumerable.Repeat("#112233", 9));
        response.FileInfo.Placeholder.AspectRatio.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(OtherUserId)]
    public async Task Handle_Avatar_IsReturnedToAnyone(long userId)
    {
        var fileId = Guid.NewGuid();
        SetupFile(ReadyFile(fileId, UploadFileType.UserAvatar, OwnerId));

        var response = await CreateSut().Handle(new GetFileDataCommand { FileId = fileId, UserId = userId }, default);

        response.FileInfo.Id.Should().Be(fileId.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(OtherUserId)]
    public async Task Handle_ForeignCloudFile_ThrowsAccessDenied(long userId)
    {
        var fileId = Guid.NewGuid();
        SetupFile(ReadyFile(fileId, UploadFileType.CloudFile, OwnerId));

        var act = () => CreateSut().Handle(new GetFileDataCommand { FileId = fileId, UserId = userId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_ForeignNotReadyFile_ThrowsAccessDeniedWithoutRevealingState()
    {
        var fileId = Guid.NewGuid();
        SetupFile(new UploadFileEntity
        {
            Id = fileId,
            Type = UploadFileType.CloudFile,
            Uploaders = new List<long> { OwnerId }
        });

        var act = () => CreateSut().Handle(new GetFileDataCommand { FileId = fileId, UserId = OtherUserId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }
}
