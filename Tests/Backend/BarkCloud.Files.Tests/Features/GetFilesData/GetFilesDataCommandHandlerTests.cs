using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFilesData;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Features.GetFilesData;

public class GetFilesDataCommandHandlerTests
{
    private const long OwnerId = 42;
    private const long OtherUserId = 7;

    private readonly Mock<IUploadedFilesStorage> _files = new();

    private GetFilesDataCommandHandler CreateSut() => new(
        _files.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<GetFilesDataCommandHandler>.Instance);

    private static UploadFileEntity ReadyFile(Guid id, UploadFileType type, params long[] uploaders) => new()
    {
        Id = id,
        Filename = "a.jpg",
        Etag = "etag",
        UploadedAt = DateTime.UtcNow,
        Type = type,
        Uploaders = uploaders.ToList()
    };

    private void SetupFiles(params UploadFileEntity[] files)
    {
        _files.Setup(s => s.GetPlaceholdersForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, FilePlaceholder>());
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(files.ToList());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
    }

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        SetupFiles();

        var response = await CreateSut().Handle(new GetFilesDataCommand { FileIds = new List<Guid>(), UserId = OwnerId }, default);

        response.FilesInfos.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OwnFiles_MapsAll()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        SetupFiles(
            ReadyFile(first, UploadFileType.CloudFile, OwnerId),
            ReadyFile(second, UploadFileType.CloudFile, OwnerId, OtherUserId));
        _files.Setup(s => s.GetPlaceholdersForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, FilePlaceholder>
            {
                [first] = new() { FileId = first, Colors = Enumerable.Repeat("#112233", 9).ToArray(), AspectRatio = 1 },
                [second] = new() { FileId = second, Colors = Enumerable.Repeat("#445566", 9).ToArray(), AspectRatio = 1.5f }
            });

        var response = await CreateSut().Handle(
            new GetFilesDataCommand { FileIds = new List<Guid> { first, second }, UserId = OwnerId }, default);

        response.FilesInfos.Should().HaveCount(2);
        response.FilesInfos.Select(f => f.Id).Should().BeEquivalentTo(new[] { first.ToString(), second.ToString() });
        response.FilesInfos.Single(f => f.Id == first.ToString()).Placeholder.Colors.Should().Equal(Enumerable.Repeat("#112233", 9));
        response.FilesInfos.Single(f => f.Id == second.ToString()).Placeholder.AspectRatio.Should().Be(1.5f);
        _files.Verify(s => s.GetPlaceholdersForFiles(It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AvatarWithoutUserId_IsReturned()
    {
        var avatarId = Guid.NewGuid();
        SetupFiles(ReadyFile(avatarId, UploadFileType.UserAvatar, OwnerId));

        var response = await CreateSut().Handle(
            new GetFilesDataCommand { FileIds = new List<Guid> { avatarId }, UserId = 0 }, default);

        response.FilesInfos.Should().ContainSingle().Which.Id.Should().Be(avatarId.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(OtherUserId)]
    public async Task Handle_ForeignCloudFile_ThrowsAccessDenied(long userId)
    {
        var foreignId = Guid.NewGuid();
        SetupFiles(ReadyFile(foreignId, UploadFileType.CloudFile, OwnerId));

        var act = () => CreateSut().Handle(
            new GetFilesDataCommand { FileIds = new List<Guid> { foreignId }, UserId = userId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_AccessibleAndForeignFiles_ThrowsAccessDeniedForWholeRequest()
    {
        var ownId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        SetupFiles(
            ReadyFile(ownId, UploadFileType.CloudFile, OtherUserId),
            ReadyFile(foreignId, UploadFileType.CloudFile, OwnerId));

        var act = () => CreateSut().Handle(
            new GetFilesDataCommand { FileIds = new List<Guid> { ownId, foreignId }, UserId = OtherUserId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }
}
