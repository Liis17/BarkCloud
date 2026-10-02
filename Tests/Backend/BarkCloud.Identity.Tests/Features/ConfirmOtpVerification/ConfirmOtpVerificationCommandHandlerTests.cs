using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmOtpVerification;
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

using OtpNet;

using PersistenceOtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.ConfirmOtpVerification;

public class ConfirmOtpVerificationCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ConfirmOtpVerificationCommandHandler> _logger =
        NullLogger<ConfirmOtpVerificationCommandHandler>.Instance;

    public ConfirmOtpVerificationCommandHandlerTests()
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

    private ConfirmOtpVerificationCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _authProps.Object,
        _usersClient.Object,
        _notifications.Object,
        new RequestContext { DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1" },
        _location.Object,
        _metrics,
        _rateLimiter.Object,
        _logger);

    [Fact]
    public async Task Handle_PersistenceOtpNotCreated_ThrowsDomainOtpNotCreated()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Email
        });
        _authProps
            .Setup(s => s.TryConsumeEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, "654321"))
            .ReturnsAsync(true);
        _authProps.Setup(s => s.EnableEmailOtp(42)).ThrowsAsync(new PersistenceOtpNotCreatedException());

        var act = () => CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "654321" }, default);

        await act.Should().ThrowAsync<BarkCloud.Shared.Exceptions.Identity.OtpNotCreatedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorNoPendingSecret_ThrowsOtpNotCreated()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator
        });

        var act = () => CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "000000" }, default);

        await act.Should().ThrowAsync<BarkCloud.Shared.Exceptions.Identity.OtpNotCreatedException>();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorExpiredPendingSecret_ThrowsOtpNotCreated()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = Base32Encoding.ToString(key),
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });

        // Код сам по себе верный — отказ только из-за истёкшего срока ожидающего секрета.
        var act = () => CreateSut().Handle(
            new ConfirmOtpVerificationCommand { OtpCode = new Totp(key).ComputeTotp() }, default);

        await act.Should().ThrowAsync<BarkCloud.Shared.Exceptions.Identity.OtpNotCreatedException>();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorInvalidCode_ThrowsNotValidOtp()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = secret,
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        var act = () => CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "000000" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorCodeOfActiveSecret_RejectedWhileNewSecretPending()
    {
        var activeKey = KeyGeneration.GenerateRandomKey(20);
        var pendingKey = KeyGeneration.GenerateRandomKey(20);
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(activeKey),
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = Base32Encoding.ToString(pendingKey),
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        // Код старого приложения не подтверждает новый секрет.
        var act = () => CreateSut().Handle(
            new ConfirmOtpVerificationCommand { OtpCode = new Totp(activeKey).ComputeTotp() }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorValidCode_ActivatesPendingSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        var validCode = new Totp(key).ComputeTotp();

        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = secret,
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        _authProps.Setup(s => s.ActivatePendingOtpSecret(42, secret)).ReturnsAsync(true);

        var response = await CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = validCode }, default);

        response.Should().NotBeNull();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(42, secret), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_enabled_authenticator");
    }

    [Fact]
    public async Task Handle_AuthenticatorPendingSecretReplacedMeanwhile_ThrowsNotValidOtp()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);

        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = secret,
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        // Между проверкой кода и активацией ожидающий секрет заменили (параллельный Enable).
        _authProps.Setup(s => s.ActivatePendingOtpSecret(42, secret)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new ConfirmOtpVerificationCommand { OtpCode = new Totp(key).ComputeTotp() }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailInvalidCode_ThrowsNotValidOtp()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Email
        });
        _authProps
            .Setup(s => s.TryConsumeEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, "999999"))
            .ReturnsAsync(false);

        var act = () => CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "999999" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _authProps.Verify(s => s.EnableEmailOtp(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailValidCode_EnablesEmailOtp()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Email
        });
        _authProps
            .Setup(s => s.TryConsumeEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, "654321"))
            .ReturnsAsync(true);

        await CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "654321" }, default);

        _authProps.Verify(s => s.EnableEmailOtp(42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_enabled_email");
    }

    [Fact]
    public async Task Handle_NoOtpTypeSelected_ReturnsResponseWithoutSideEffects()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Unknown
        });

        await CreateSut().Handle(new ConfirmOtpVerificationCommand { OtpCode = "0" }, default);

        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _authProps.Verify(s => s.EnableEmailOtp(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorTotpLimitExceeded_RejectsValidCodeWithoutActivation()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = Base32Encoding.ToString(key),
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        _rateLimiter.Setup(l => l.EnsureTotpAttemptAsync(42)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(
            new ConfirmOtpVerificationCommand { OtpCode = new Totp(key).ComputeTotp() }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }
}
