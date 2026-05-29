using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.UpdateProfileServer;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.UpdateProfileServer;

public class UpdateProfileServerCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();
    private readonly Mock<UserInfoQueueSender> _queue;

    public UpdateProfileServerCommandHandlerTests()
    {
        _queue = new Mock<UserInfoQueueSender>(Mock.Of<IPublishEndpoint>(), new BarkCloud.GrpcServer.Metrics.MetricsCollector());
        _queue.Setup(s => s.NameChangedEvent(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _queue.Setup(s => s.UsernameChangedEvent(It.IsAny<long>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    private UpdateProfileServerCommandHandler CreateSut() => new(
        _users.Object,
        _queue.Object,
        NullLogger<UpdateProfileServerCommandHandler>.Instance);

    private void SetupCurrent(string firstName = "Bark", string lastName = "Dog", string username = "barker")
        => _users.Setup(s => s.GetById(7)).ReturnsAsync(new User
        {
            Id = 7, FirstName = firstName, LastName = lastName, Username = username, RegistrationDate = DateTime.UtcNow
        });

    [Fact]
    public async Task Handle_UserNotFound_Throws()
    {
        _users.Setup(s => s.GetById(7)).ReturnsAsync((User?)null);

        var act = () => CreateSut().Handle(new UpdateProfileServerCommand { UserId = 7, FirstName = "X" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_NameChanged_CallsChangeNameAndPublishes()
    {
        SetupCurrent();

        await CreateSut().Handle(new UpdateProfileServerCommand
        {
            UserId = 7, FirstName = " New ", LastName = " Name "
        }, default);

        _users.Verify(s => s.ChangeName(7, "New", "Name"), Times.Once);
        _queue.Verify(s => s.NameChangedEvent(7, "New", "Name"), Times.Once);
        _users.Verify(s => s.ChangeUsername(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UsernameChanged_CallsChangeUsername()
    {
        SetupCurrent();

        await CreateSut().Handle(new UpdateProfileServerCommand { UserId = 7, Username = "newname" }, default);

        _users.Verify(s => s.ChangeUsername(7, "newname"), Times.Once);
        _queue.Verify(s => s.UsernameChangedEvent(7, "newname"), Times.Once);
        _users.Verify(s => s.ChangeName(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoEffectiveChange_DoesNothing()
    {
        SetupCurrent(firstName: "Bark", lastName: "Dog", username: "barker");

        await CreateSut().Handle(new UpdateProfileServerCommand
        {
            UserId = 7, FirstName = "Bark", LastName = "Dog", Username = "barker"
        }, default);

        _users.Verify(s => s.ChangeName(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _users.Verify(s => s.ChangeUsername(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }
}
