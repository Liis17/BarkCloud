using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.ConfirmResetPassword;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.ConfirmResetPassword;

public class ConfirmResetPasswordCommandHandlerTests
{
    private readonly Mock<IResetPasswordsStorage> _resets = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Mock<PasswordChangedNotifier> _notifier;
    private readonly JwtSettings _jwt = new() { SecretKey = "k", Issuer = "i", Audience = "a", ExpiryMinutes = 15 };
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ConfirmResetPasswordCommandHandler> _logger = NullLogger<ConfirmResetPasswordCommandHandler>.Instance;

    public ConfirmResetPasswordCommandHandlerTests()
    {
        _notifier = new Mock<PasswordChangedNotifier>(
            Mock.Of<UsersServerApi.UsersServerApiClient>(),
            new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Object,
            new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance).Object,
            new RequestContext(),
            NullLogger<PasswordChangedNotifier>.Instance);
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).Returns(Task.CompletedTask);

        _refreshTokens.Setup(s => s.DeleteAllByUserId(42)).ReturnsAsync(new List<string>());
        _resets.Setup(s => s.TryApprove(It.IsAny<Guid>())).ReturnsAsync(true);
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });
    }

    private ConfirmResetPasswordCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _resets.Object, _authProps.Object, _passwords.Object, _refreshTokens.Object,
        _mediator.Object, _publish.Object, _jwt, _notifier.Object, ctx ?? FullContext(), _metrics, _logger);

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
        _refreshTokens.Verify(s => s.DeleteAllByUserId(It.IsAny<long>()), Times.Never);
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
        _refreshTokens.Verify(s => s.DeleteAllByUserId(It.IsAny<long>()), Times.Never);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RevokeOtherSessionsByDefault_RevokesBeforeSettingPasswordAndSkipsCurrentDevice()
    {
        var id = Guid.NewGuid();
        var calls = new List<string>();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _refreshTokens.Setup(s => s.DeleteAllByUserId(42))
            .Callback(() => calls.Add("revoke"))
            .ReturnsAsync(new List<string> { "device-1", "device-2", "device-3" });
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .Callback(() => calls.Add("set-hash"))
            .ReturnsAsync(false);

        await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        calls.Should().Equal("revoke", "set-hash");
        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.UserId == 42 && e.DeviceId == "device-2"),
            It.IsAny<CancellationToken>()), Times.Once);
        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.UserId == 42 && e.DeviceId == "device-3"),
            It.IsAny<CancellationToken>()), Times.Once);
        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.DeviceId == "device-1"),
            It.IsAny<CancellationToken>()), Times.Never);
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

        _refreshTokens.Verify(s => s.DeleteAllByUserId(It.IsAny<long>()), Times.Never);
        _publish.Verify(p => p.Publish(It.IsAny<SessionRevokedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotificationFails_StillReturnsTokens()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(ValidEmailReset(id));
        _notifier.Setup(n => n.NotifyAsync(42)).ThrowsAsync(new InvalidOperationException("smtp down"));

        var response = await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456", NewPassword = "newp" }, default);

        response.AccessToken.Value.Should().Be("access");
    }
}
