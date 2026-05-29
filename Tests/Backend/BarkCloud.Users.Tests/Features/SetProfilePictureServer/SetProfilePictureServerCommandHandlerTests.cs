using BarkCloud.Users.Features.SetProfilePictureServer;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.SetProfilePictureServer;

public class SetProfilePictureServerCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();
    private readonly Mock<UserInfoQueueSender> _queue;

    public SetProfilePictureServerCommandHandlerTests()
    {
        _queue = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new BarkCloud.GrpcServer.Metrics.MetricsCollector());
        _queue.Setup(s => s.UserChangedAvatarEvent(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private SetProfilePictureServerCommandHandler CreateSut() => new(
        _users.Object,
        _queue.Object,
        NullLogger<SetProfilePictureServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UpdatesPictureAndPublishesEvent()
    {
        await CreateSut().Handle(new SetProfilePictureServerCommand
        {
            UserId = 13,
            ProfilePictureUrl = "u",
            ProfilePicturePreviewUrl = "p"
        }, default);

        _users.Verify(s => s.UpdateProfilePicture(13, "u", "p"), Times.Once);
        _queue.Verify(s => s.UserChangedAvatarEvent(13, "u", "p"), Times.Once);
    }
}
