using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.ConfirmResetPassword;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using DomainAuthUserProperty = BarkCloud.Identity.Domain.AuthUserProperty;
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.ConfirmResetPassword;

public class ConfirmResetPasswordCommandHandlerTests : IDisposable
{
    private readonly Mock<IResetPasswordsStorage> _resets = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<IWebAuthnStorage> _webAuthn = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<PasswordChangedNotifier> _notifier;
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();
    private readonly ILogger<ConfirmResetPasswordCommandHandler> _logger = NullLogger<ConfirmResetPasswordCommandHandler>.Instance;

    public ConfirmResetPasswordCommandHandlerTests()
    {
        _notifier = new Mock<PasswordChangedNotifier>(Mock.Of<INotificationOutbox>(), new RequestContext());
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).ReturnsAsync(true);

        _refreshTokens.Setup(s => s.RevokeAllSessions(42, "device-1", default)).ReturnsAsync(0);
        _webAuthn.Setup(s => s.RemoveAllCredentials(42, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _resets.Setup(s => s.TryApprove(It.IsAny<Guid>())).ReturnsAsync(true);
        _resets.Setup(s => s.TryReserveOtpAttempt(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
        _authProps.Setup(s => s.GetUserAuthPropertiesSnapshot(42, It.IsAny<CancellationToken>()))
            .Returns((long userId, CancellationToken cancellationToken) =>
                _authProps.Object.GetUserAuthProperties(userId, cancellationToken));
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
    }

    private ConfirmResetPasswordCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _resets.Object, _authProps.Object, _passwords.Object, _refreshTokens.Object,
        _webAuthn.Object, _mediator.Object, _notifier.Object, ctx ?? FullContext(), _metrics, _rateLimiter.Object,
        _logger, _db.Context);

    private static DomainResetPassword ValidEmailReset(Guid id) => new()
    {
        Id = id,
        UserId = 42,
        ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        OtpType = OtpType.Email,
        OtpCode = "123456"
    };

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new ConfirmResetPasswordCommand { ResetId = Guid.NewGuid(), OtpCode = "0" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_ResetIdNotFound_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync((DomainResetPassword?)null);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdNotFoundException>();
    }

    [Fact]
    public async Task Handle_AlreadyApproved_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            IsApproved = true,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdHasIsApprovedException>();
    }

    [Fact]
    public async Task Handle_Expired_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdExpiredException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorWrongCode_Throws()
    {
        var id = Guid.NewGuid();
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Authenticator
        });
        _authProps.Setup(s => s.GetOtpSecretKey(42)).ReturnsAsync(secret);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "000000" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailWrongCode_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Email,
            OtpCode = "123456"
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "999999" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailValidCode_SetsNewPasswordAndReturnsTokens()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var response = await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _resets.Verify(s => s.TryApprove(id), Times.Once);
        _passwords.Verify(s => s.UpdateUserPasswordHash(42,
            It.Is<string>(h => PasswordHasher.VerifyPassword("newp", h))), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 42, "device-1", It.IsAny<int>()), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_resets_confirmed");
    }

    [Fact]
    public async Task Handle_NewPasswordEmpty_ThrowsAndDoesNotConsumeReset()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "" }, default);

        await act.Should().ThrowAsync<NewPasswordRequiredException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NewPasswordSameAsCurrent_ThrowsAndDoesNotConsumeReset()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "oldp" }, default);

        await act.Should().ThrowAsync<NewPasswordSameAsOldException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _refreshTokens.Verify(s => s.RevokeAllSessions(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ResetClaimedByParallelRequest_ThrowsWithoutChangingPassword()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _resets.Setup(s => s.TryApprove(id)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<ResetIdHasIsApprovedException>();
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _refreshTokens.Verify(s => s.RevokeAllSessions(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RevokeOtherSessionsByDefault_RevokesBeforeSettingPasswordAndSkipsCurrentDevice()
    {
        var id = Guid.NewGuid();
        var calls = new List<string>();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _refreshTokens.Setup(s => s.RevokeAllSessions(42, "device-1", default))
            .Callback(() => calls.Add("revoke"))
            .ReturnsAsync(2);
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .Callback(() => calls.Add("set-hash"))
            .ReturnsAsync(false);

        await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        calls.Should().Equal("revoke", "set-hash");
        _refreshTokens.Verify(s => s.RevokeAllSessions(42, "device-1", default), Times.Once);
    }

    [Fact]
    public async Task Handle_RevokeOtherSessionsDisabled_KeepsExistingSessions()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        await CreateSut().Handle(
            new ConfirmResetPasswordCommand
            {
                ResetId = id, OtpCode = "123456", NewPassword = "newp", RevokeOtherSessions = false
            }, default);

        _refreshTokens.Verify(s => s.RevokeAllSessions(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ResetChoices_ApplyBothFlags(bool resetPasskeys, bool revokeSessions)
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        await CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id,
            OtpCode = "123456",
            NewPassword = "newp",
            ResetPasskeys = resetPasskeys,
            RevokeOtherSessions = revokeSessions
        }, default);

        _webAuthn.Verify(s => s.RemoveAllCredentials(42, It.IsAny<CancellationToken>()),
            resetPasskeys ? Times.Once : Times.Never);
        _refreshTokens.Verify(s => s.RevokeAllSessions(42, "device-1", It.IsAny<CancellationToken>()),
            revokeSessions ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task Handle_ResetPasskeysByDefault_RemovesKeysAndRevokesSessions()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        await CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "123456", NewPassword = "newp"
        }, default);

        _webAuthn.Verify(s => s.RemoveAllCredentials(42, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokens.Verify(s => s.RevokeAllSessions(42, "device-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotificationFails_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _notifier.Setup(n => n.NotifyAsync(42)).ThrowsAsync(new InvalidOperationException("smtp down"));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_SourceLimitExceeded_ThrowsBeforeResetLookup()
    {
        _rateLimiter.Setup(l => l.EnsureSourceAsync(AuthLimits.ConfirmResetPasswordByIp)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = Guid.NewGuid(), OtpCode = "123456", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _resets.Verify(s => s.GetResetPassword(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailWrongCode_ReservesAttemptOnThatResetBeforeComparison()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "000000", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryReserveOtpAttempt(id, AuthLimits.ChallengeMaxAttempts), Times.Once);
    }

    [Fact]
    public async Task Handle_EmailAttemptsExhausted_RejectsEvenCorrectCodeWithoutChangingPassword()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _resets.Setup(s => s.TryReserveOtpAttempt(id, It.IsAny<int>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("password_reset_confirmation_failed_attempts_exceeded");
    }

    [Fact]
    public async Task Handle_AuthenticatorAttemptsExhausted_RejectsEvenCorrectTotp()
    {
        var id = Guid.NewGuid();
        var key = KeyGeneration.GenerateRandomKey(20);
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id, UserId = 42, ExpiresAt = DateTime.UtcNow.AddMinutes(5), OtpType = OtpType.Authenticator
        });
        _authProps.Setup(s => s.GetOtpSecretKey(42)).ReturnsAsync(Base32Encoding.ToString(key));
        _resets.Setup(s => s.TryReserveOtpAttempt(id, It.IsAny<int>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = new Totp(key).ComputeTotp(), NewPassword = "newp"
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("")]
    public async Task Handle_EmailCodeOfDifferentLength_IsRejected(string code)
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = code, NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailResetWithoutStoredCode_RejectsEmptyCode()
    {
        var id = Guid.NewGuid();
        var reset = ValidEmailReset(id);
        reset.OtpCode = null;
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(reset);

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    private static (string Secret, Totp Totp) AuthenticatorSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        return (Base32Encoding.ToString(key), new Totp(key));
    }

    private void SetupAuthenticator(string? secret, bool enabled = true)
        => _authProps.Setup(s => s.GetUserAuthProperties(42))
            .ReturnsAsync(new DomainAuthUserProperty { UserId = 42, OtpEnabled = enabled, OtpSecret = secret });

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabled_WithoutTotp_ThrowsOtpNeededAndChangesNothing()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, _) = AuthenticatorSecret();
        SetupAuthenticator(secret);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>()
            .Where(e => e.ErrorMetadata["x-otp-method"] == "authenticator");
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _refreshTokens.Verify(s => s.RevokeAllSessions(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<long>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("auth_otp_required");
    }

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabled_WrongTotp_ThrowsInvalidCodeAndChangesNothing()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, _) = AuthenticatorSecret();
        SetupAuthenticator(secret);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "123456", NewPassword = "newp", AuthenticatorCode = "000000"
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabled_ValidTotp_SetsNewPasswordAndReturnsTokens()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, totp) = AuthenticatorSecret();
        SetupAuthenticator(secret);
        _authProps.Setup(s => s.TryConsumeTotpStep(42, secret, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var response = await CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "123456", NewPassword = "newp", AuthenticatorCode = totp.ComputeTotp()
        }, default);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _resets.Verify(s => s.TryApprove(id), Times.Once);
        _passwords.Verify(s => s.UpdateUserPasswordHash(42,
            It.Is<string>(h => PasswordHasher.VerifyPassword("newp", h))), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_resets_confirmed")
            .And.ContainKey("otp_authenticator_verified");
    }

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabled_NewPasswordSameAsOld_RequiresTotpFirst()
    {
        // Порядок проверок: требование Authenticator возвращается до сравнения с прежним паролем,
        // чтобы ответ не раскрывал, совпадает ли новый пароль с текущим.
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, _) = AuthenticatorSecret();
        SetupAuthenticator(secret);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "oldp" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabled_WrongEmailCode_RejectsBeforeReadingAuthenticator()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, totp) = AuthenticatorSecret();
        SetupAuthenticator(secret);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "999999", NewPassword = "newp", AuthenticatorCode = totp.ComputeTotp()
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _authProps.Verify(s => s.GetUserAuthProperties(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailResetWithAuthenticatorEnabledButNoSecret_RejectsSafely()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        SetupAuthenticator(secret: null);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "123456", NewPassword = "newp", AuthenticatorCode = "123456"
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryReserveOtpAttempt(id, It.IsAny<int>()), Times.Once);
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailResetWithCorruptedSecret_RejectsSafely()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        SetupAuthenticator("!!не-base32!!");

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = "123456", NewPassword = "newp", AuthenticatorCode = "123456"
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryApprove(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailResetWithoutAuthenticatorSettings_KeepsPreviousBehavior()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((DomainAuthUserProperty?)null);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var response = await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        response.AccessToken.Value.Should().Be("access");
        _resets.Verify(s => s.TryApprove(id), Times.Once);
    }

    [Fact]
    public async Task Handle_EmailResetAuthenticatorDisabled_KeepsPreviousBehavior()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        var (secret, _) = AuthenticatorSecret();
        SetupAuthenticator(secret, enabled: false);

        var response = await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        response.AccessToken.Value.Should().Be("access");
        _resets.Verify(s => s.TryApprove(id), Times.Once);
    }

    [Fact]
    public async Task Handle_PreviouslyIssuedAuthenticatorOnlyResetIsRejected()
    {
        // Ранее созданный reset ID также не должен позволять сбросить пароль одним TOTP.
        var id = Guid.NewGuid();
        var (secret, totp) = AuthenticatorSecret();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id, UserId = 42, ExpiresAt = DateTime.UtcNow.AddMinutes(5), OtpType = OtpType.Authenticator
        });
        _authProps.Setup(s => s.GetOtpSecretKey(42)).ReturnsAsync(secret);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand
        {
            ResetId = id, OtpCode = totp.ComputeTotp(), NewPassword = "newp", AuthenticatorCode = "000000"
        }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _resets.Verify(s => s.TryReserveOtpAttempt(id, It.IsAny<int>()), Times.Never);
        _resets.Verify(s => s.TryApprove(id), Times.Never);
    }
}
