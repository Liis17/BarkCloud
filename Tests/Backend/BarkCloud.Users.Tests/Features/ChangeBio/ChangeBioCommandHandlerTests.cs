using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Users.Features.ChangeBio;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.ChangeBio;

public class ChangeBioCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _usersStorage = new();
    private readonly Mock<UserInfoQueueSender> _queueSender;

    public ChangeBioCommandHandlerTests()
    {
        _queueSender = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new MetricsCollector());
        _queueSender
            .Setup(s => s.BioChangedEvent(It.IsAny<long>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private ChangeBioCommandHandler CreateSut(long userId = 42) => new(
        UserContextFactory.Create(userId),
        _usersStorage.Object,
        _queueSender.Object,
        NullLogger<ChangeBioCommandHandler>.Instance);

    [Fact]
    public async Task Handle_BioTooLong_Throws()
    {
        var act = () => CreateSut().Handle(new ChangeBioCommand { Bio = new string('a', 201) }, default);

        await act.Should().ThrowAsync<BioTooLongException>();
        _usersStorage.Verify(s => s.ChangeBio(It.IsAny<long>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NormalBio_PersistsAndPublishes()
    {
        await CreateSut().Handle(new ChangeBioCommand { Bio = "  hello  " }, default);

        _usersStorage.Verify(s => s.ChangeBio(42, "hello"), Times.Once);
        _queueSender.Verify(s => s.BioChangedEvent(42, "hello"), Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyBio_StoresNullAndPublishesEmpty()
    {
        await CreateSut().Handle(new ChangeBioCommand { Bio = "   " }, default);

        _usersStorage.Verify(s => s.ChangeBio(42, null), Times.Once);
        _queueSender.Verify(s => s.BioChangedEvent(42, string.Empty), Times.Once);
    }
}
