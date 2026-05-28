using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Users.Features.DeleteAccount;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.DeleteAccount;

public class DeleteAccountCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();
    private readonly Mock<UserInfoQueueSender> _queueSender;
    private readonly MetricsCollector _metrics = new();

    public DeleteAccountCommandHandlerTests()
    {
        _queueSender = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new MetricsCollector());
        _queueSender
            .Setup(s => s.UserDeletedEvent(It.IsAny<long>()))
            .Returns(Task.CompletedTask);
    }

    private DeleteAccountCommandHandler CreateSut(long userId = 42) => new(
        _usersStorage.Object,
        UserContextFactory.Create(userId),
        _queueSender.Object,
        _metrics,
        NullLogger<DeleteAccountCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DeletesUserAndPublishesEvent()
    {
        await CreateSut().Handle(new DeleteAccountCommand(), default);

        _usersStorage.Verify(s => s.DeleteUser(42), Times.Once);
        _queueSender.Verify(s => s.UserDeletedEvent(42), Times.Once);
        _metrics.SnapshotAndReset()["accounts_deleted"].Should().Be(1);
    }
}
