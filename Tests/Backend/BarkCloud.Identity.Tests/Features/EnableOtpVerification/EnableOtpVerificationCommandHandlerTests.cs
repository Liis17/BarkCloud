using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.EnableOtpVerification;
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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.EnableOtpVerification;

public class EnableOtpVerificationCommandHandlerTests
{
    private const string Password = "correct-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);

    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<EnableOtpVerificationCommandHandler> _logger = NullLogger<EnableOtpVerificationCommandHandler>.Instance;

    public EnableOtpVerificationCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
        _passwords.Setup(p => p.GetUserPasswordHash(42)).ReturnsAsync(PasswordHash);
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(true);
    }

    private static IConfiguration EmailConfig(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = enabled ? "true" : "false" })
        .Build();

    private EnableOtpVerificationCommandHandler CreateSut(RequestContext? ctx = null, UserContext? user = null, bool emailEnabled = true)
        => new(user ?? UserContextFactory.Create(42),
            _authProps.Object,
            new ReauthPasswordVerifier(_authProps.Object, _passwords.Object),
            _usersClient.Object,
            _notifications.Object,
            ctx ?? FullContext(),
            _location.Object,
            _metrics,
            _rateLimiter.Object,
            EmailConfig(emailEnabled),
            _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        IpAddress = "127.0.0.1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoOperationSystem_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = null };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XOsNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoAppInfo_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = "A" };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XAppInfoIsRequiedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorType_StoresPendingSecretAndReturnsQr()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, Password = Password }, default);

        response.OtpCode.Should().NotBeNullOrEmpty();
        response.OtpQr.Should().NotBeNullOrEmpty();
        _authProps.Verify(s => s.SetPendingOtpSecret(
            42, response.OtpCode, It.Is<DateTime>(d => d > DateTime.UtcNow && d <= DateTime.UtcNow.AddMinutes(11))), Times.Once);
        _authProps.Verify(s => s.UpdateOptType(OtpType.Authenticator, 42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_setup_authenticator");
    }

    [Fact]
    public async Task Handle_AuthenticatorType_ActiveAuthenticator_ValidCurrentCode_StoresOnlyPendingSecret()
    {
        var activeKey = KeyGeneration.GenerateRandomKey(20);
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(activeKey)
        });

        var response = await CreateSut().Handle(new EnableOtpVerificationCommand
        {
            OptType = OtpTypeId.Authenticator,
            Password = Password,
            CurrentOtpCode = new Totp(activeKey).ComputeTotp()
        }, default);

        // Действующий секрет остаётся прежним: новый ждёт подтверждения в отдельном поле.
        _authProps.Verify(s => s.SetPendingOtpSecret(42, response.OtpCode, It.IsAny<DateTime>()), Times.Once);
        _authProps.Verify(s => s.ActivatePendingOtpSecret(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _authProps.Verify(s => s.DisableOtp(It.IsAny<long>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-password")]
    public async Task Handle_AuthenticatorType_MissingOrWrongPassword_ThrowsWithoutWrites(string? password)
    {
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var act = () => CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, Password = password }, default);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        _authProps.Verify(s => s.UpdateOptType(It.IsAny<OtpType>(), It.IsAny<long>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_setup_failed_invalid_password");
    }

    [Fact]
    public async Task Handle_AuthenticatorType_AttemptLimitExceeded_ThrowsWithoutWritesEvenWithCorrectPassword()
    {
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, Password = Password }, default);

        await act.Should().ThrowAsync<PasswordAttemptsExceededException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        _authProps.Verify(s => s.UpdateOptType(It.IsAny<OtpType>(), It.IsAny<long>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_setup_failed_invalid_password");
    }

    [Fact]
    public async Task Handle_AuthenticatorType_CorrectPassword_ResetsAttemptCounter()
    {
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, Password = Password }, default);

        _authProps.Verify(s => s.ResetReauthPasswordAttempts(42), Times.Once);
    }

    [Fact]
    public async Task Handle_AuthenticatorType_NoPasswordHash_ThrowsWithoutWrites()
    {
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);
        _passwords.Setup(p => p.GetUserPasswordHash(42)).ReturnsAsync((string?)null);

        var act = () => CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, Password = Password }, default);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    public async Task Handle_AuthenticatorType_ActiveAuthenticator_MissingOrWrongCurrentCode_ThrowsWithoutWrites(string? currentCode)
    {
        var activeKey = KeyGeneration.GenerateRandomKey(20);
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(activeKey)
        });

        var act = () => CreateSut().Handle(new EnableOtpVerificationCommand
        {
            OptType = OtpTypeId.Authenticator,
            Password = Password,
            CurrentOtpCode = currentCode
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        _authProps.Verify(s => s.UpdateOptType(It.IsAny<OtpType>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorType_ActiveAuthenticator_WrongPasswordValidCode_ThrowsInvalidPassword()
    {
        var activeKey = KeyGeneration.GenerateRandomKey(20);
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(activeKey)
        });

        // Верный код при неверном пароле: отказ по паролю, секрет не меняется.
        var act = () => CreateSut().Handle(new EnableOtpVerificationCommand
        {
            OptType = OtpTypeId.Authenticator,
            Password = "wrong-password",
            CurrentOtpCode = new Totp(activeKey).ComputeTotp()
        }, default);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    private void ArrangeUser() => _usersClient
        .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
        .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));

    [Fact]
    public async Task Handle_EmailType_SendsNotificationAndStoresCode()
    {
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
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);
        _authProps
            .Setup(s => s.TryIssueEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, It.IsAny<string>()))
            .ReturnsAsync(true);

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Email }, default);

        response.OtpQr.Should().BeEmpty();
        _authProps.Verify(s => s.TryIssueEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, It.IsAny<string>()), Times.Once);
        _authProps.Verify(s => s.UpdateOptType(OtpType.Email, 42), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.ConfirmationOtpEmail)), Times.Once);
    }

    [Fact]
    public async Task Handle_EmailType_ResendCooldown_DoesNotSendNotification()
    {
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
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);
        _authProps
            .Setup(s => s.TryIssueEmailAuthCode(42, EmailAuthCodePurpose.EnableEmailOtp, It.IsAny<string>()))
            .ReturnsAsync(false);

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Email }, default);

        response.OtpQr.Should().BeEmpty();
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailType_EmailDisabled_Throws()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));

        var act = () => CreateSut(emailEnabled: false).Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Email }, default);

        await act.Should().ThrowAsync<EmailServiceDisabledException>();
        _authProps.Verify(s => s.UpdateOptType(OtpType.Email, It.IsAny<long>()), Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UnknownType_ReturnsEmptyQrWithoutSideEffects()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Unknown }, default);

        response.OtpQr.Should().BeEmpty();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        _authProps.Verify(
            s => s.TryIssueEmailAuthCode(It.IsAny<long>(), It.IsAny<EmailAuthCodePurpose>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AuthenticatorType_CurrentCodeLimitExceeded_RejectsValidCodeWithoutWrites()
    {
        var activeKey = KeyGeneration.GenerateRandomKey(20);
        ArrangeUser();
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(activeKey)
        });
        _rateLimiter.Setup(l => l.EnsureTotpAttemptAsync(42)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(new EnableOtpVerificationCommand
        {
            OptType = OtpTypeId.Authenticator,
            Password = Password,
            CurrentOtpCode = new Totp(activeKey).ComputeTotp()
        }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _authProps.Verify(s => s.SetPendingOtpSecret(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }
}
