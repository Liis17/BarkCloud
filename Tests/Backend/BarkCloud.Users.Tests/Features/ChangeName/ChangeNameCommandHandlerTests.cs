using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Users.Features.ChangeName;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.ChangeName;

public class ChangeNameCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();
    private readonly Mock<UserInfoQueueSender> _queueSender;

    public ChangeNameCommandHandlerTests()
    {
        _queueSender = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new MetricsCollector());
        _queueSender
            .Setup(s => s.NameChangedEvent(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private ChangeNameCommandHandler CreateSut(long userId = 42) => new(
        UserContextFactory.Create(userId),
        _usersStorage.Object,
        _queueSender.Object,
        NullLogger<ChangeNameCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UpdatesNameAndPublishesEvent()
    {
        await CreateSut().Handle(new ChangeNameCommand { FirstName = "John", LastName = "Doe" }, default);

        _usersStorage.Verify(s => s.ChangeName(42, "John", "Doe"), Times.Once);
        _queueSender.Verify(s => s.NameChangedEvent(42, "John", "Doe"), Times.Once);
    }

    [Fact]
    public async Task Handle_TrimsNames()
    {
        await CreateSut().Handle(new ChangeNameCommand { FirstName = "  John  ", LastName = "  Doe  " }, default);

        _usersStorage.Verify(s => s.ChangeName(42, "John", "Doe"), Times.Once);
        _queueSender.Verify(s => s.NameChangedEvent(42, "John", "Doe"), Times.Once);
    }
}
