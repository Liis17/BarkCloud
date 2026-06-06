using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.SetPassword;

public class SetPasswordCommandHandlerTests
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<SetPasswordCommandHandler> _logger = NullLogger<SetPasswordCommandHandler>.Instance;

    public SetPasswordCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);

        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
    }

    private SetPasswordCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _passwords.Object,
        _refreshTokens.Object,
        _notifications.Object,
        _location.Object,
        _usersClient.Object,
        new RequestContext { DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1" },
        _metrics,
        _logger);

    [Fact]
    public async Task Handle_OldHashSetButOldPasswordEmpty_Throws()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(new SetPasswordCommand { NewPassword = "new" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
    }

    [Fact]
    public async Task Handle_OldPasswordIncorrect_Throws()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new SetPasswordCommand { OldPassword = "wrong", NewPassword = "new" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
    }

    [Fact]
    public async Task Handle_ValidChange_UpdatesHashAndSendsNotification()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .ReturnsAsync(false);

        await CreateSut().Handle(new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.PasswordChanged)), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_changes");
    }

    [Fact]
    public async Task Handle_InitialPasswordSet_DoesNotSendNotification()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync((string?)null);
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .ReturnsAsync(true);

        await CreateSut().Handle(new SetPasswordCommand { NewPassword = "newp" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.IsAny<EmailNotification>()), Times.Never);
        var snap = _metrics.SnapshotAndReset();
        snap.Should().ContainKey("password_changes");
        snap.Should().ContainKey("password_changes_initial");
    }
}
