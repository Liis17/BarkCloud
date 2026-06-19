using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.CreateAccount;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.CreateAccount;

public class CreateAccountCommandHandlerTests
{
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IConfirmationCodesStorage> _codes = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<CreateAccountCommandHandler> _logger = NullLogger<CreateAccountCommandHandler>.Instance;

    public CreateAccountCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
    }

    private static IConfiguration FeatureConfig(bool emailEnabled, bool registrationEnabled = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:EmailEnabled"] = emailEnabled ? "true" : "false",
            ["Features:RegistrationEnabled"] = registrationEnabled ? "true" : "false"
        })
        .Build();

    private CreateAccountCommandHandler CreateSut(
        RequestContext? ctx = null, bool emailEnabled = true, bool registrationEnabled = true) => new(
        _usersClient.Object, _codes.Object, _notifications.Object,
        ctx ?? FullContext(), _location.Object, _metrics, _refreshTokens.Object,
        FeatureConfig(emailEnabled, registrationEnabled), _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android 14",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        IpAddress = "127.0.0.1"
    };

    private static CreateAccountCommand ValidCommand() => new()
    {
        Username = "user",
        Email = "u@e",
        FirstName = "First",
        LastName = "Last"
    };

    [Fact]
    public async Task Handle_EmptyEmail_Throws()
    {
        var act = () => CreateSut().Handle(new CreateAccountCommand { Username = "u" }, default);

        await act.Should().ThrowAsync<UsernameOrEmailIsEmptyException>();
    }

    [Fact]
    public async Task Handle_EmptyUsername_Throws()
    {
        var act = () => CreateSut().Handle(new CreateAccountCommand { Email = "u@e" }, default);

        await act.Should().ThrowAsync<UsernameOrEmailIsEmptyException>();
    }

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null, OperationSystem = "A", AppName = "B", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(ValidCommand(), default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoOperationSystem_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = null, AppName = "B", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(ValidCommand(), default);

        await act.Should().ThrowAsync<XOsNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoAppInfo_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = "A", AppName = null, AppVersion = null };

        var act = () => CreateSut(ctx).Handle(ValidCommand(), default);

        await act.Should().ThrowAsync<XAppInfoIsRequiedException>();
    }

    [Fact]
    public async Task Handle_RegistrationDisabled_ThrowsWithoutCreatingDraft()
    {
        var act = () => CreateSut(registrationEnabled: false).Handle(ValidCommand(), default);

        await act.Should().ThrowAsync<RegistrationDisabledException>();
        _usersClient.Verify(c => c.AddDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default), Times.Never);
        _codes.Verify(s => s.AddCode(It.IsAny<ConfirmationCode>()), Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }
    [Fact]
    public async Task Handle_HappyPath_AddsCodeAndReturnsCodeId()
    {
        var codeId = Guid.NewGuid();
        _usersClient
            .Setup(c => c.AddDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new AddDraftUserResponse { UserId = 7 }));
        _codes
            .Setup(s => s.AddCode(It.IsAny<ConfirmationCode>()))
            .ReturnsAsync((ConfirmationCode c) => { c.Id = codeId; return c; });

        var response = await CreateSut().Handle(ValidCommand(), default);

        response.CodeId.Should().Be(codeId.ToString());
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.ConfirmationRegistration && e.OwnerId == 7)), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("accounts_drafted");
    }

    [Fact]
    public async Task Handle_UserIsDraftException_FallsBackToOverride()
    {
        _usersClient
            .Setup(c => c.AddDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default))
            .Throws(new UserIsDraftException());
        _usersClient
            .Setup(c => c.OverrideDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new AddDraftUserResponse { UserId = 11 }));
        _codes
            .Setup(s => s.AddCode(It.IsAny<ConfirmationCode>()))
            .ReturnsAsync((ConfirmationCode c) => { c.Id = Guid.NewGuid(); return c; });

        await CreateSut().Handle(ValidCommand(), default);

        _usersClient.Verify(c => c.OverrideDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("accounts_draft_overridden");
    }

    [Fact]
    public async Task Handle_EmailDisabled_ConfirmsImmediately_ReturnsRefresh_NoCode_NoEmail()
    {
        _usersClient
            .Setup(c => c.AddDraftUserAsync(It.IsAny<AddDraftUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new AddDraftUserResponse { UserId = 7 }));
        _usersClient
            .Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmUserResponse()));

        var response = await CreateSut(emailEnabled: false).Handle(ValidCommand(), default);

        // Аккаунт подтверждён сразу, выдан refresh; код не создаётся, письмо не публикуется.
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        response.CodeId.Should().BeNullOrEmpty();
        _usersClient.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 7), null, null, default), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, It.IsAny<string>(), It.IsAny<int>()), Times.Once);
        _codes.Verify(s => s.AddCode(It.IsAny<ConfirmationCode>()), Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        var snap = _metrics.SnapshotAndReset();
        snap.Should().ContainKey("accounts_confirmed");
        snap.Should().ContainKey("sessions_created");
    }
}
