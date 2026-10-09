using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.Auth;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;
using BarkCloud.Identity.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.Auth;

public class AuthCommandHandlerTests : IDisposable
{
    private static readonly string PasswordHash = BCrypt.Net.BCrypt.HashPassword("p");

    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<INotificationOutbox> _outbox = new();
    private readonly Mock<SessionIssuer> _sessions;
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<LocationClient> _location;
    private readonly Mock<IAuthRateLimiter> _rateLimiter = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<AuthCommandHandler> _logger = NullLogger<AuthCommandHandler>.Instance;
    private readonly SqliteIdentityContext _database = new();

    public void Dispose() => _database.Dispose();

    public AuthCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        _location = new Mock<LocationClient>(
            new HttpClient(),
            new MetricsCollector(),
            NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);

        _sessions = new Mock<SessionIssuer>(
            Mock.Of<UsersServerApi.UsersServerApiClient>(), Mock.Of<MediatR.IMediator>(), _outbox.Object,
            Mock.Of<Identity.Persistence.Services.IRefreshTokensStorage>(), _database.Context, new RequestContext(), _location.Object,
            _metrics, NullLogger<SessionIssuer>.Instance);
        _sessions.Setup(s => s.IssueAsync(It.IsAny<long>(), It.IsAny<CancellationToken>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .Returns(async (long _, CancellationToken token, Func<CancellationToken, Task>? completeAuthentication) =>
            {
                if (completeAuthentication is not null)
                {
                    await completeAuthentication(token);
                }

                return new AuthResponse
                {
                    AccessToken = new Token { Value = "access" },
                    RefreshToken = new Token { Value = "refresh" }
                };
            });

        // По умолчанию лимиты не сработали; отдельные тесты переопределяют нужную политику.
        _rateLimiter.Setup(l => l.TryReserveAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>())).ReturnsAsync(true);
    }

    private AuthCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _usersClient.Object, _authProps.Object, _notifications.Object, _outbox.Object, _sessions.Object,
        ctx ?? FullContext(), _passwords.Object, _location.Object, _metrics, _rateLimiter.Object, _logger);

    private static RequestContext FullContext(string? sourceIp = null) => new()
    {
        SourceIp = sourceIp ?? "203.0.113.7",
        DeviceName = "Pixel",
        OperationSystem = "Android 14",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1",
        IpAddress = "127.0.0.1"
    };

    [Fact]
    public async Task Handle_NoUsernameAndEmail_Throws()
    {
        var act = () => CreateSut().Handle(new AuthCommand { Password = "p" }, default);

        await act.Should().ThrowAsync<NotSetUsernameOrEmailException>();
    }

    [Fact]
    public async Task Handle_NoPassword_Throws()
    {
        var act = () => CreateSut().Handle(new AuthCommand { Username = "u" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null, OperationSystem = "Android", AppName = "A", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoOperationSystem_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = null, AppName = "A", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XOsNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoAppName_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = "Android", AppName = null, AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XAppInfoIsRequiedException>();
    }

    [Fact]
    public async Task Handle_UserNotFound_ThrowsInvalidLoginOrPassword()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse()));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_UserNotFound_IncrementsFailureMetrics()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse()));

        await Assert.ThrowsAsync<InvalidLoginOrPasswordException>(
            () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default));

        var snap = _metrics.SnapshotAndReset();
        snap["auth_login_failed"].Should().Be(1);
        snap["auth_login_failed_user_not_found"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_TotpEnabledAndNoCode_ThrowsOtpCodeNeed()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse
            {
                User = new User { Id = 1, Username = "u" }
            }));
        _authProps
            .Setup(s => s.GetUserAuthProperties(1))
            .ReturnsAsync(new AuthUserProperty { UserId = 1, OtpEnabled = true });
        _passwords.Setup(s => s.GetUserPasswordHash(1)).ReturnsAsync(PasswordHash);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
    }

    [Fact]
    public async Task Handle_TotpEnabledNoCodeWrongPassword_ThrowsInvalidLoginNotOtpCodeNeed()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, OtpEnabled = true });

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _authProps.Verify(s => s.GetUserAuthProperties(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailOtpNoCodeWrongPassword_DoesNotIssueOrSendCode()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _authProps.Verify(
            s => s.TryIssueEmailAuthCode(It.IsAny<long>(), It.IsAny<EmailAuthCodePurpose>(), It.IsAny<string>()),
            Times.Never);
        _notifications.Verify(
            n => n.SendNotification(It.Is<EmailNotification>(e => e.Type == NotificationType.ConfirmationAuth)),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EmailOtpNoCode_IssuesLoginCodeSendsEmailAndThrowsOtpCodeNeed()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });
        _authProps
            .Setup(s => s.TryIssueEmailAuthCode(1, EmailAuthCodePurpose.Login, It.IsAny<string>()))
            .ReturnsAsync(true);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
        _authProps.Verify(s => s.TryIssueEmailAuthCode(1, EmailAuthCodePurpose.Login, It.IsAny<string>()), Times.Once);
        _notifications.Verify(
            n => n.SendNotification(It.Is<EmailNotification>(e => e.Type == NotificationType.ConfirmationAuth)),
            Times.Once);
    }

    [Fact]
    public async Task Handle_EmailOtpNoCodeResendCooldown_DoesNotSendEmailButThrowsOtpCodeNeed()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });
        _authProps
            .Setup(s => s.TryIssueEmailAuthCode(1, EmailAuthCodePurpose.Login, It.IsAny<string>()))
            .ReturnsAsync(false);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailOtpInvalidCode_ThrowsNotValidOtpAndDoesNotIssueTokens()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });
        _authProps
            .Setup(s => s.TryValidateAndReserveEmailAuthCode(1, EmailAuthCodePurpose.Login, "123456", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidatedEmailAuthCode?)null);

        var act = () => CreateSut().Handle(
            new AuthCommand { Username = "u", Password = "p", OtpCode = "123456" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
        _sessions.Verify(s => s.IssueAsync(It.IsAny<long>(), It.IsAny<CancellationToken>(), It.IsAny<Func<CancellationToken, Task>?>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailOtpValidCode_ConsumesCodeAndIssuesTokens()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });
        var validatedCode = new ValidatedEmailAuthCode(1, EmailAuthCodePurpose.Login, "123456",
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5));
        _authProps
            .Setup(s => s.TryValidateAndReserveEmailAuthCode(1, EmailAuthCodePurpose.Login, "123456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(validatedCode);
        _authProps.Setup(s => s.TryConsumeValidatedEmailAuthCode(validatedCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await CreateSut().Handle(
            new AuthCommand { Username = "u", Password = "p", OtpCode = "123456" }, default);

        response.AccessToken.Value.Should().Be("access");
        _authProps.Verify(s => s.TryValidateAndReserveEmailAuthCode(
            1, EmailAuthCodePurpose.Login, "123456", It.IsAny<CancellationToken>()), Times.Once);
        _authProps.Verify(s => s.TryConsumeValidatedEmailAuthCode(validatedCode, It.IsAny<CancellationToken>()), Times.Once);
        _sessions.Verify(s => s.IssueAsync(1, It.IsAny<CancellationToken>(), It.IsAny<Func<CancellationToken, Task>?>()), Times.Once);
    }

    /// <summary>Пользователь с паролем "p" и всем необходимым для успешного входа.</summary>
    private void SetupUser(long id, AuthUserProperty? props)
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = id, Username = "u" } }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = id, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));

        _authProps.Setup(s => s.GetUserAuthProperties(id)).ReturnsAsync(props);
        _passwords.Setup(s => s.GetUserPasswordHash(id)).ReturnsAsync(PasswordHash);
    }

    [Fact]
    public async Task Handle_WrongPassword_ThrowsInvalidLoginOrPassword()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse
            {
                User = new User { Id = 1, Username = "u" }
            }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 1, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
        _authProps.Setup(s => s.GetUserAuthProperties(1)).ReturnsAsync((AuthUserProperty?)null);
        _passwords.Setup(s => s.GetUserPasswordHash(1))
            .ReturnsAsync(BCrypt.Net.BCrypt.HashPassword("correct-password"));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ReturnsTokensFromSharedSessionIssuer()
    {
        SetupUser(42, null);
        using var cts = new CancellationTokenSource();

        var response = await CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, cts.Token);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().Be("refresh");
        _sessions.Verify(s => s.IssueAsync(42, cts.Token, It.IsAny<Func<CancellationToken, Task>?>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HappyPath_DoesNotSendLoginMailItself()
    {
        SetupUser(42, null);

        await CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        // Письмо о входе ставит в очередь SessionIssuer, а не синхронная отправка из хендлера.
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        _usersClient.Verify(
            c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task Handle_SessionIssuerFails_PropagatesException()
    {
        SetupUser(42, null);
        _sessions.Setup(s => s.IssueAsync(It.IsAny<long>(), It.IsAny<CancellationToken>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ───────── F14: лимиты попыток ─────────

    [Fact]
    public async Task Handle_SourceLimitExceeded_ThrowsTooManyRequestsBeforeUserLookup()
    {
        _rateLimiter.Setup(l => l.EnsureSourceAsync(AuthLimits.AuthByIp)).ThrowsAsync(new TooManyRequestsException());

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _usersClient.Verify(
            c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task Handle_InvalidRequest_DoesNotSpendSourceLimit()
    {
        var act = () => CreateSut().Handle(new AuthCommand { Username = "u" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _rateLimiter.Verify(l => l.EnsureSourceAsync(It.IsAny<AuthLimits.Policy>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AccountLimitExhausted_RejectsBeforePasswordCheckAndCodeIssue()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, EmailOtpEnabled = true });
        _rateLimiter.Setup(l => l.TryReserveAsync(AuthLimits.LoginByAccount, "1")).ReturnsAsync(false);

        // Пароль верный, но лимит исчерпан: ни пароль, ни код 2FA не проверяются, письмо не уходит.
        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<PasswordAttemptsExceededException>();
        _passwords.Verify(s => s.GetUserPasswordHash(It.IsAny<long>()), Times.Never);
        _authProps.Verify(
            s => s.TryIssueEmailAuthCode(It.IsAny<long>(), It.IsAny<EmailAuthCodePurpose>(), It.IsAny<string>()),
            Times.Never);
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        _metrics.SnapshotAndReset()["auth_login_failed_locked"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_AccountLimit_DependsOnAccountNotOnSourceIp()
    {
        SetupUser(1, null);

        foreach (var ip in new[] { "203.0.113.7", "198.51.100.9" })
        {
            var ctx = FullContext(sourceIp: ip);
            await CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);
        }

        _rateLimiter.Verify(l => l.TryReserveAsync(AuthLimits.LoginByAccount, "1"), Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_Success_ResetsAccountCounter()
    {
        SetupUser(1, null);

        await CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        _rateLimiter.Verify(l => l.ResetAsync(AuthLimits.LoginByAccount, "1"), Times.Once);
    }

    [Fact]
    public async Task Handle_WrongPassword_DoesNotResetAccountCounter()
    {
        SetupUser(1, null);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _rateLimiter.Verify(l => l.ResetAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_OtpStepWithoutFullSuccess_DoesNotResetAccountCounter()
    {
        SetupUser(1, new AuthUserProperty { UserId = 1, OtpEnabled = true });

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
        _rateLimiter.Verify(l => l.ResetAsync(It.IsAny<AuthLimits.Policy>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WrongPassword_SendsFailedLoginMailWhenNotLimited()
    {
        SetupUser(1, null);
        _outbox.Setup(o => o.EnqueueAsync(1, NotificationType.FailedLogin, It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _outbox.Verify(o => o.EnqueueAsync(
            1, NotificationType.FailedLogin, "Неуспешная попытка входа в аккаунт",
            It.Is<Dictionary<string, string>>(p => p["devicename"] == "Pixel" && p["os"] == "Android 14")), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_enqueued");
    }

    [Fact]
    public async Task Handle_WrongPassword_OutboxInsertFailureKeepsInvalidPasswordResult()
    {
        SetupUser(1, null);
        _outbox.Setup(o => o.EnqueueAsync(1, NotificationType.FailedLogin, It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.DbUpdateException("notification insert failed"));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_WrongPassword_FailedLoginMailDoesNotTouchUsersOrLocation()
    {
        SetupUser(1, null);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        _usersClient.Verify(
            c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default), Times.Never);
        _location.Verify(c => c.GetLocation(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WrongPasswordMailLimited_SkipsMailAndExternalLookups()
    {
        SetupUser(1, null);
        _rateLimiter.Setup(l => l.TryReserveAsync(AuthLimits.FailedLoginMail, "1")).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
        _outbox.Verify(o => o.EnqueueAsync(
            It.IsAny<long>(), It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _usersClient.Verify(
            c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default), Times.Never);
        _location.Verify(c => c.GetLocation(It.IsAny<string>()), Times.Never);
    }
}
