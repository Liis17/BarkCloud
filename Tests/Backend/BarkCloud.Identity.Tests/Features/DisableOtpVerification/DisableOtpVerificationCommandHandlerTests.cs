using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.DisableOtpVerification;
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

using OtpType = BarkCloud.Identity.Domain.OtpType;
using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Tests.Features.DisableOtpVerification;

public class DisableOtpVerificationCommandHandlerTests
{
    private const string Password = "correct-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);

    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<DisableOtpVerificationCommandHandler> _logger =
        NullLogger<DisableOtpVerificationCommandHandler>.Instance;

    public DisableOtpVerificationCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
        _passwords.Setup(p => p.GetUserPasswordHash(42)).ReturnsAsync(PasswordHash);

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

    private DisableOtpVerificationCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _authProps.Object,
        _passwords.Object,
        _notifications.Object,
        _location.Object,
        _usersClient.Object,
        new RequestContext { DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1" },
        _metrics,
        _logger);

    [Fact]
    public async Task Handle_NoAuthProperties_Throws()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = Password }, default);

        await act.Should().ThrowAsync<OtpNotCreatedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorRequestedButNotEnabled_Throws()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = false
        });

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, OtpCode = "000000" },
            default);

        await act.Should().ThrowAsync<OtpNotCreatedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorInvalidCode_Throws()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);

        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = secret
        });

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, OtpCode = "000000" },
            default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorValidCode_DisablesOtpAndSendsNotification()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        var validCode = new Totp(key).ComputeTotp();

        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = secret
        });

        await CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, OtpCode = validCode },
            default);

        _authProps.Verify(s => s.DisableOtp(42), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.TwoFactorMethodChanged)), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_disabled_authenticator");
    }

    [Fact]
    public async Task Handle_EmailType_DisablesEmailOtpAndSendsNotification()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            EmailOtpEnabled = true
        });

        await CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = Password },
            default);

        _authProps.Verify(s => s.DisableEmailOtp(42), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.IsAny<EmailNotification>()), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_disabled_email");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-password")]
    public async Task Handle_EmailType_MissingOrWrongPassword_ThrowsAndKeepsEmailOtp(string? password)
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            EmailOtpEnabled = true
        });

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = password },
            default);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.DisableEmailOtp(It.IsAny<long>()), Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_disable_failed");
    }

    [Fact]
    public async Task Handle_EmailType_NoPasswordHash_ThrowsAndKeepsEmailOtp()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            EmailOtpEnabled = true
        });
        _passwords.Setup(p => p.GetUserPasswordHash(42)).ReturnsAsync((string?)null);

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = Password },
            default);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.DisableEmailOtp(It.IsAny<long>()), Times.Never);
    }
}
