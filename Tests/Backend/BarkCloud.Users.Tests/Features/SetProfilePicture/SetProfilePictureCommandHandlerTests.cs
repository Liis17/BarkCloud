using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.TestKit;
using BarkCloud.Users.Features.SetProfilePicture;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.SetProfilePicture;

public class SetProfilePictureCommandHandlerTests
{
    private readonly Mock<FilesServerApi.FilesServerApiClient> _filesClient = new();
    private readonly Mock<IUsersStorage> _users = new();
    private readonly Mock<UserInfoQueueSender> _queue;
    private readonly MetricsCollector _metrics = new();

    public SetProfilePictureCommandHandlerTests()
    {
        _queue = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new MetricsCollector());
        _queue.Setup(s => s.UserChangedAvatarEvent(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private SetProfilePictureCommandHandler CreateSut(long userId = 42) => new(
        _filesClient.Object,
        _users.Object,
        UserContextFactory.Create(userId),
        _queue.Object,
        _metrics,
        NullLogger<SetProfilePictureCommandHandler>.Instance);

    private void SetupFile(UploadFileType type, string fileUrl = "url", string previewUrl = "preview")
    {
        var response = new GetFileDataResponse
        {
            FileInfo = new UploadFileInfo { Type = type, FileUrl = fileUrl, PreviewUrl = previewUrl }
        };
        _filesClient.Setup(c => c.GetFileDataAsync(
                It.IsAny<GetFileDataRequest>(), It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(response));
    }

    [Fact]
    public async Task Handle_NullFileId_ClearsAvatarWithoutCallingFiles()
    {
        await CreateSut().Handle(new SetProfilePictureCommand { FileId = null }, default);

        _users.Verify(s => s.UpdateProfilePicture(42, "", ""), Times.Once);
        _queue.Verify(s => s.UserChangedAvatarEvent(42, "", ""), Times.Once);
        _filesClient.Verify(c => c.GetFileDataAsync(
            It.IsAny<GetFileDataRequest>(), It.IsAny<Grpc.Core.Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidAvatar_SetsUrlsFromFileInfo()
    {
        SetupFile(UploadFileType.UserAvatar, "https://cdn/u", "https://cdn/p");

        await CreateSut().Handle(new SetProfilePictureCommand { FileId = Guid.NewGuid() }, default);

        _users.Verify(s => s.UpdateProfilePicture(42, "https://cdn/u", "https://cdn/p"), Times.Once);
        _queue.Verify(s => s.UserChangedAvatarEvent(42, "https://cdn/u", "https://cdn/p"), Times.Once);
    }

    [Fact]
    public async Task Handle_WrongFileType_ThrowsAndDoesNotUpdate()
    {
        SetupFile(UploadFileType.CloudFile);

        var act = () => CreateSut().Handle(new SetProfilePictureCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<ProfilePictureHasNotValidType>();
        _users.Verify(s => s.UpdateProfilePicture(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FilesServiceFails_PropagatesAndDoesNotUpdate()
    {
        _filesClient.Setup(c => c.GetFileDataAsync(
                It.IsAny<GetFileDataRequest>(), It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Internal, "files down")));

        var act = () => CreateSut().Handle(new SetProfilePictureCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
        _users.Verify(s => s.UpdateProfilePicture(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _queue.Verify(s => s.UserChangedAvatarEvent(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
