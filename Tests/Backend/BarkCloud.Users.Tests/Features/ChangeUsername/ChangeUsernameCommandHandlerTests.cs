using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Features.ChangeUsername;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.ChangeUsername;

public class ChangeUsernameCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();
    private readonly Mock<UserInfoQueueSender> _queueSender;

    public ChangeUsernameCommandHandlerTests()
    {
        _queueSender = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new MetricsCollector());
        _queueSender
            .Setup(s => s.UsernameChangedEvent(It.IsAny<long>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private ChangeUsernameCommandHandler CreateSut(long userId = 42, params string[] reserved) => new(
        UserContextFactory.Create(userId),
        _usersStorage.Object,
        ReservedUsernamesFactory.Create(reserved),
        _queueSender.Object,
        NullLogger<ChangeUsernameCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ReservedUsername_ThrowsAndDoesNotUpdate()
    {
        var sut = CreateSut(42, "admin");

        var act = () => sut.Handle(new ChangeUsernameCommand { Username = "Admin" }, default);

        await act.Should().ThrowAsync<UsernameReservedException>();
        _usersStorage.Verify(s => s.ChangeUsername(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _queueSender.Verify(s => s.UsernameChangedEvent(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UpdatesUsernameAndPublishesEvent()
    {
        await CreateSut().Handle(new ChangeUsernameCommand { Username = "  john  " }, default);

        _usersStorage.Verify(s => s.ChangeUsername(42, "john"), Times.Once);
        _queueSender.Verify(s => s.UsernameChangedEvent(42, "john"), Times.Once);
    }
}
