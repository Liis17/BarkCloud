using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.BeginReauthentication;
using BarkCloud.Identity.Features.CompleteReauthentication;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.BeginReauthentication;

public sealed class BeginReauthenticationEmailCooldownTests : IDisposable
{
    private const long UserId = 7;
    private const long SessionId = 55;
    private const string Password = "known-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task BeginWithoutChallengeId_ReusesPendingIssueAndKeepsAttemptsDuringCooldown()
    {
        await SeedEmailUserAsync();
        var (handler, outbox) = CreateBeginHandler();
        var first = await BeginEmailChallengeAsync(handler);
        var storage = new AuthPropertiesStorage(_db.Context);
        var issued = await LoadAuthPropertiesAsync();
        var wrongCode = issued.LastEmailAuthCode == "000000" ? "000001" : "000000";
        (await storage.TryValidateAndReserveEmailAuthCode(UserId, EmailAuthCodePurpose.Reauthentication,
            wrongCode, requestId: Guid.Parse(first.ChallengeId))).Should().BeNull();
        var beforeResend = await LoadAuthPropertiesAsync();

        var second = await BeginEmailChallengeAsync(handler);

        second.ChallengeId.Should().Be(first.ChallengeId);
        second.RetryAfterSeconds.Should().BeInRange(1, 60);
        var afterResend = await LoadAuthPropertiesAsync();
        afterResend.LastEmailAuthCode.Should().Be(beforeResend.LastEmailAuthCode);
        afterResend.EmailAuthCodeIssuedAt.Should().Be(beforeResend.EmailAuthCodeIssuedAt);
        afterResend.EmailAuthCodeAttempts.Should().Be(1);
        outbox.Verify(s => s.EnqueueAsync(UserId, NotificationType.ConfirmationAuth,
            "Код повторной аутентификации", It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BeginAfterFiveWrongCompletions_DoesNotResetAttemptsDuringCooldown()
    {
        await SeedEmailUserAsync();
        var (beginHandler, outbox) = CreateBeginHandler();
        var challenge = await BeginEmailChallengeAsync(beginHandler);
        var properties = await LoadAuthPropertiesAsync();
        var wrongCode = properties.LastEmailAuthCode == "000000" ? "000001" : "000000";
        var completeHandler = CreateCompleteHandler();
        var request = new CompleteReauthenticationCommand
        {
            ChallengeId = challenge.ChallengeId,
            EmailCode = wrongCode
        };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<NotValidOtpCodeException>(
                () => completeHandler.Handle(request, CancellationToken.None));
        }

        var beforeSecondBegin = await LoadAuthPropertiesAsync();
        beforeSecondBegin.EmailAuthCodeAttempts.Should().Be(5);
        var second = await BeginEmailChallengeAsync(beginHandler);

        second.ChallengeId.Should().Be(challenge.ChallengeId);
        second.RetryAfterSeconds.Should().BeInRange(1, 60);
        var afterSecondBegin = await LoadAuthPropertiesAsync();
        afterSecondBegin.LastEmailAuthCode.Should().Be(beforeSecondBegin.LastEmailAuthCode);
        afterSecondBegin.EmailAuthCodeIssuedAt.Should().Be(beforeSecondBegin.EmailAuthCodeIssuedAt);
        afterSecondBegin.EmailAuthCodeAttempts.Should().Be(5);
        await Assert.ThrowsAsync<NotValidOtpCodeException>(
            () => completeHandler.Handle(request, CancellationToken.None));
        (await LoadAuthPropertiesAsync()).EmailAuthCodeAttempts.Should().Be(5);
        outbox.Verify(s => s.EnqueueAsync(UserId, NotificationType.ConfirmationAuth,
            "Код повторной аутентификации", It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResendWithChallengeId_ReusesSamePendingIssueDuringCooldown()
    {
        await SeedEmailUserAsync();
        var (handler, outbox) = CreateBeginHandler();
        var first = await BeginEmailChallengeAsync(handler);
        var beforeResend = await LoadAuthPropertiesAsync();

        var resend = await handler.Handle(new BeginReauthenticationCommand
        {
            Action = ReauthenticationAction.AddPasskey,
            ChallengeId = first.ChallengeId
        }, CancellationToken.None);

        resend.ChallengeId.Should().Be(first.ChallengeId);
        resend.RetryAfterSeconds.Should().BeInRange(1, 60);
        var afterResend = await LoadAuthPropertiesAsync();
        afterResend.LastEmailAuthCode.Should().Be(beforeResend.LastEmailAuthCode);
        afterResend.EmailAuthCodeIssuedAt.Should().Be(beforeResend.EmailAuthCodeIssuedAt);
        afterResend.EmailAuthCodeAttempts.Should().Be(0);
        outbox.Verify(s => s.EnqueueAsync(UserId, NotificationType.ConfirmationAuth,
            "Код повторной аутентификации", It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private (BeginReauthenticationCommandHandler Handler, Mock<INotificationOutbox> Outbox) CreateBeginHandler()
    {
        var authPropertiesStorage = new AuthPropertiesStorage(_db.Context);
        var passwordsStorage = new PasswordsStorage(_db.Context);
        var outbox = new Mock<INotificationOutbox>();
        outbox.Setup(s => s.EnqueueAsync(UserId, It.IsAny<NotificationType>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var usersClient = new Mock<UsersServerApi.UsersServerApiClient>();
        usersClient.Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null,
                It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = UserId, Username = "user" },
                Contact = new UserContact { Email = "user@example.test" }
            }));
        var handler = new BeginReauthenticationCommandHandler(
            CreateUserContext(),
            _db.Context,
            authPropertiesStorage,
            passwordsStorage,
            new ReauthPasswordVerifier(authPropertiesStorage, passwordsStorage),
            Mock.Of<IWebAuthnStorage>(),
            new ReauthenticationService(_db.Context, passwordsStorage),
            Mock.Of<IFido2>(),
            usersClient.Object,
            outbox.Object,
            Mock.Of<IAuthRateLimiter>(),
            new MetricsCollector(),
            new RequestContext(),
            NullLogger<BeginReauthenticationCommandHandler>.Instance);
        return (handler, outbox);
    }

    private CompleteReauthenticationCommandHandler CreateCompleteHandler()
    {
        var authPropertiesStorage = new AuthPropertiesStorage(_db.Context);
        var passwordsStorage = new PasswordsStorage(_db.Context);
        return new CompleteReauthenticationCommandHandler(
            CreateUserContext(),
            _db.Context,
            authPropertiesStorage,
            passwordsStorage,
            Mock.Of<IWebAuthnStorage>(),
            new ReauthenticationService(_db.Context, passwordsStorage),
            Mock.Of<IFido2>(),
            NullLogger<CompleteReauthenticationCommandHandler>.Instance);
    }

    private async Task<BeginReauthenticationResponse> BeginEmailChallengeAsync(
        BeginReauthenticationCommandHandler handler) => await handler.Handle(new BeginReauthenticationCommand
    {
        Action = ReauthenticationAction.AddPasskey,
        Password = Password
    }, CancellationToken.None);

    private async Task<AuthUserProperty> LoadAuthPropertiesAsync()
    {
        using var context = _db.CreateAdditionalContext();
        return await context.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
    }

    private async Task SeedEmailUserAsync()
    {
        _db.Context.AuthUserProperties.Add(new AuthUserProperty
        {
            UserId = UserId,
            EmailOtpEnabled = true,
            SelectedOtpType = OtpType.Email
        });
        _db.Context.UserPasswords.Add(new UserPassword
        {
            UserId = UserId,
            PasswordHash = PasswordHash,
            ChangedAt = DateTime.UtcNow,
            SecurityStateVersion = 3
        });
        _db.Context.RefreshTokens.Add(new RefreshToken
        {
            Id = SessionId,
            UserId = UserId,
            Value = "refresh-token",
            DeviceId = "device-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await _db.Context.SaveChangesAsync();
    }

    private static UserContext CreateUserContext()
    {
        var claims = new List<Claim>
        {
            new(IdentityClaims.UserId, UserId.ToString()),
            new(IdentityClaims.TokenType, TokenType.User.ToString()),
            new(IdentityClaims.DeviceId, "device-1"),
            new(IdentityClaims.SessionId, SessionId.ToString())
        };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
        return new UserContext(accessor);
    }
}
