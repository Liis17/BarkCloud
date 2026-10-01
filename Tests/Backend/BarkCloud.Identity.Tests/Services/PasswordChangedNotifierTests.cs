using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Services;

public class PasswordChangedNotifierTests
{
    [Fact]
    public async Task NotifyAsync_SendsPasswordChangedEmailToUserAddress()
    {
        var usersClient = new Mock<UsersServerApi.UsersServerApiClient>();
        usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));
        usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));

        var notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        var location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);

        var sut = new PasswordChangedNotifier(
            usersClient.Object,
            notifications.Object,
            location.Object,
            new RequestContext { DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1" },
            NullLogger<PasswordChangedNotifier>.Instance);

        await sut.NotifyAsync(42);

        notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.PasswordChanged && e.OwnerId == 42 && e.Address == "u@e")), Times.Once);
    }
}
