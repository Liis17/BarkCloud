using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.DisableOtpVerification;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using OtpType = BarkCloud.Identity.Domain.OtpType;
using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Tests.Features.DisableOtpVerification;

public class DisableOtpVerificationCommandHandlerTests : IDisposable
{
    private readonly SqliteIdentityContext _database = new();
    private const string Password = "correct-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);

    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<INotificationOutbox> _outbox = new();
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<DisableOtpVerificationCommandHandler> _logger =
        NullLogger<DisableOtpVerificationCommandHandler>.Instance;

    public DisableOtpVerificationCommandHandlerTests()
    {
        _passwords.Setup(p => p.GetUserPasswordHash(42)).ReturnsAsync(PasswordHash);
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(true);
    }

    private void VerifyNothingQueued() => _outbox.Verify(o => o.EnqueueAsync(
        It.IsAny<long>(), It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);

    private DisableOtpVerificationCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _database.Context,
        _authProps.Object,
        new ReauthPasswordVerifier(_authProps.Object, _passwords.Object),
        _outbox.Object,
        new RequestContext { DeviceName = "Pixel", OperationSystem = "Android", IpAddress = "1.1.1.1" },
        _metrics,
        _rateLimiter.Object,
        _logger);

    public void Dispose() => _database.Dispose();

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
        _outbox.Verify(o => o.EnqueueAsync(
            42, NotificationType.TwoFactorMethodChanged, It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(p => p["old_method"] == "Authenticator приложение"
                                                   && p["new_method"] == "Отключена" && p["devicename"] == "Pixel")), Times.Once);
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
        _outbox.Verify(o => o.EnqueueAsync(
            42, NotificationType.TwoFactorMethodChanged, It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(p => p["old_method"] == "Email" && p["new_method"] == "Отключена")), Times.Once);
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
        VerifyNothingQueued();
        _metrics.SnapshotAndReset().Should().ContainKey("otp_disable_failed");
    }

    [Fact]
    public async Task Handle_EmailType_AttemptLimitExceeded_ThrowsAndKeepsEmailOtpEvenWithCorrectPassword()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            EmailOtpEnabled = true
        });
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = Password },
            default);

        await act.Should().ThrowAsync<PasswordAttemptsExceededException>();
        _authProps.Verify(s => s.DisableEmailOtp(It.IsAny<long>()), Times.Never);
        VerifyNothingQueued();
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

    [Fact]
    public async Task Handle_AuthenticatorTotpLimitExceeded_RejectsValidCodeWithoutDisabling()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            OtpSecret = Base32Encoding.ToString(key)
        });
        _rateLimiter.Setup(l => l.EnsureTotpAttemptAsync(42)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, OtpCode = new Totp(key).ComputeTotp() },
            default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _authProps.Verify(s => s.DisableOtp(It.IsAny<long>()), Times.Never);
    }
}
