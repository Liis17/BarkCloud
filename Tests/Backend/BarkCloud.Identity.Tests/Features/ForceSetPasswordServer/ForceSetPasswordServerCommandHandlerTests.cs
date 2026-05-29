using BarkCloud.Identity.Features.ForceSetPasswordServer;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ForceSetPasswordServer;

public class ForceSetPasswordServerCommandHandlerTests
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;

    public ForceSetPasswordServerCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 7, Username = "barker" } }));
    }

    private ForceSetPasswordServerCommandHandler CreateSut() => new(
        _passwords.Object,
        _notifications.Object,
        _usersClient.Object,
        NullLogger<ForceSetPasswordServerCommandHandler>.Instance);

    private void SetupContacts(string email) => _usersClient
        .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
        .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
        {
            User = new User { Id = 7, Username = "barker" },
            Contact = new UserContact { Email = email }
        }));

    [Fact]
    public async Task Handle_UpdatesHashAndSendsNotification()
    {
        SetupContacts("u@e");

        await CreateSut().Handle(new ForceSetPasswordServerCommand { UserId = 7, NewPassword = "secret" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(7, It.Is<string>(h => h != "secret" && h.Length > 0)), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.PasswordChangedByAdmin && e.Address == "u@e")), Times.Once);
    }

    [Fact]
    public async Task Handle_NoEmail_UpdatesHashWithoutNotification()
    {
        SetupContacts("");

        await CreateSut().Handle(new ForceSetPasswordServerCommand { UserId = 7, NewPassword = "secret" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(7, It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }
}
