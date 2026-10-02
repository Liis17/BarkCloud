using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmAccount;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ConfirmAccount;

public class ConfirmAccountCommandHandlerTests
{
    private readonly Mock<IConfirmationCodesStorage> _codes = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ConfirmAccountCommandHandler> _logger = NullLogger<ConfirmAccountCommandHandler>.Instance;

    public ConfirmAccountCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
        _codes.Setup(s => s.TryReserveAttempt(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
    }

    private ConfirmAccountCommandHandler CreateSut(RequestContext? ctx = null, bool registrationEnabled = true) => new(
        _codes.Object, _usersClient.Object, _refreshTokens.Object, ctx ?? FullContext(),
        _notifications.Object, _location.Object, _metrics, RegistrationPolicy(registrationEnabled), _rateLimiter.Object, _logger);

    private static IRegistrationPolicy RegistrationPolicy(bool enabled)
    {
        var policy = new Mock<IRegistrationPolicy>();
        if (enabled)
            policy.Setup(p => p.EnsureRegistrationEnabledAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        else
            policy.Setup(p => p.EnsureRegistrationEnabledAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new RegistrationDisabledException());

        return policy.Object;
    }

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1",
        IpAddress = "1.1.1.1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_RegistrationDisabled_ThrowsWithoutConfirming()
    {
        var act = () => CreateSut(registrationEnabled: false).Handle(new ConfirmAccountCommand
        {
            CodeId = Guid.NewGuid().ToString(),
            Code = "123456"
        }, default);

        await act.Should().ThrowAsync<RegistrationDisabledException>();

        _codes.Verify(s => s.GetCode(It.IsAny<Guid>()), Times.Never);
        _usersClient.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), null, null, default), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CodeNotFound_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync((ConfirmationCode?)null);

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeNotFoundException>();
    }

    [Fact]
    public async Task Handle_CodeOfWrongType_ThrowsNotFound()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.Unknown,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Value = "0"
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeNotFoundException>();
    }

    [Fact]
    public async Task Handle_CodeExpired_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddMinutes(-1),
            Value = "0"
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeExpiredException>();
    }

    [Fact]
    public async Task Handle_IncorrectCode_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Value = "12345",
            OwnerId = 1
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "wrong" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeIncorrectException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ConfirmsUserDeletesCodeAndReturnsRefresh()
    {
        var codeId = Guid.NewGuid();
        _codes.Setup(s => s.GetCode(codeId)).ReturnsAsync(new ConfirmationCode
        {
            Id = codeId,
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddHours(1),
            Value = "123456",
            OwnerId = 42
        });
        _usersClient
            .Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmUserResponse()));
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

        var response = await CreateSut().Handle(
            new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" },
            default);

        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _usersClient.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 42), null, null, default), Times.Once);
        _codes.Verify(s => s.DeleteCode(codeId), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 42, "device-1", It.IsAny<int>()), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap.Should().ContainKey("accounts_confirmed");
        snap.Should().ContainKey("sessions_created");
    }

    private static ConfirmationCode ValidCode(Guid id) => new()
    {
        Id = id,
        Type = ConfirmationCodeType.Registration,
        Expires = DateTime.UtcNow.AddHours(1),
        Value = "123456",
        OwnerId = 42
    };

    [Fact]
    public async Task Handle_SourceLimitExceeded_ThrowsBeforeCodeLookup()
    {
        _rateLimiter.Setup(l => l.EnsureSourceAsync(AuthLimits.ConfirmAccountByIp)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(
            new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "123456" }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _codes.Verify(s => s.GetCode(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WrongCode_ReservesAttemptOnThatCodeBeforeComparison()
    {
        var codeId = Guid.NewGuid();
        _codes.Setup(s => s.GetCode(codeId)).ReturnsAsync(ValidCode(codeId));

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "000000" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeIncorrectException>();
        _codes.Verify(s => s.TryReserveAttempt(codeId, AuthLimits.ChallengeMaxAttempts), Times.Once);
    }

    [Fact]
    public async Task Handle_AttemptsExhausted_RejectsEvenCorrectCodeWithoutConfirming()
    {
        var codeId = Guid.NewGuid();
        _codes.Setup(s => s.GetCode(codeId)).ReturnsAsync(ValidCode(codeId));
        _codes.Setup(s => s.TryReserveAttempt(codeId, It.IsAny<int>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeIncorrectException>();
        _usersClient.Verify(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), null, null, default), Times.Never);
        _codes.Verify(s => s.DeleteCode(It.IsAny<Guid>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("account_confirmation_failed_attempts_exceeded");
    }

    [Fact]
    public async Task Handle_AttemptLimit_DoesNotDependOnSourceIp()
    {
        var codeId = Guid.NewGuid();
        _codes.Setup(s => s.GetCode(codeId)).ReturnsAsync(ValidCode(codeId));

        foreach (var ip in new[] { "203.0.113.7", "198.51.100.9" })
        {
            var ctx = FullContext();
            var other = new RequestContext
            {
                DeviceName = ctx.DeviceName, OperationSystem = ctx.OperationSystem, AppName = ctx.AppName,
                AppVersion = ctx.AppVersion, DeviceId = ctx.DeviceId, SourceIp = ip
            };
            var act = () => CreateSut(other).Handle(new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "000000" }, default);
            await act.Should().ThrowAsync<ConfirmationCodeIncorrectException>();
        }

        // Счётчик привязан к коду: обе попытки с разных адресов заняты на одном и том же code_id.
        _codes.Verify(s => s.TryReserveAttempt(codeId, AuthLimits.ChallengeMaxAttempts), Times.Exactly(2));
    }
}
