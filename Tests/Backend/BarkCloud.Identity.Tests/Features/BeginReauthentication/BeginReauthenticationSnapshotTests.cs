using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.BeginReauthentication;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Features.BeginReauthentication;

public sealed class BeginReauthenticationSnapshotTests : IDisposable
{
    private const long UserId = 7;
    private const long SessionId = 55;
    private const string Password = "known-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PasswordGrant_AuthenticatorEnabledAfterInitialRead_IsRejected()
    {
        await SeedUserAsync(new AuthUserProperty { UserId = UserId });
        var initialRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = CreateBarrierStorage(initialRead, continueRequest);
        var handler = CreateHandler(storage.Object).Handler;

        var operation = handler.Handle(new BeginReauthenticationCommand
        {
            Action = ReauthenticationAction.AddPasskey,
            Password = Password
        }, CancellationToken.None);
        await initialRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var parallelContext = _db.CreateAdditionalContext())
        {
            var properties = await parallelContext.AuthUserProperties.SingleAsync(x => x.UserId == UserId);
            properties.OtpEnabled = true;
            await parallelContext.SaveChangesAsync();
        }

        continueRequest.TrySetResult(true);
        var exception = await Assert.ThrowsAsync<OtpCodeNeedException>(() => operation);
        exception.ErrorMetadata["x-otp-method"].Should().Be("authenticator");
    }

    [Fact]
    public async Task EmailChallenge_AuthenticatorEnabledAfterInitialRead_IsRejected()
    {
        await SeedUserAsync(new AuthUserProperty { UserId = UserId, EmailOtpEnabled = true });
        var initialRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = CreateBarrierStorage(initialRead, continueRequest);
        var handler = CreateHandler(storage.Object).Handler;

        var operation = handler.Handle(new BeginReauthenticationCommand
        {
            Action = ReauthenticationAction.AddPasskey,
            Password = Password
        }, CancellationToken.None);
        await initialRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var parallelContext = _db.CreateAdditionalContext())
        {
            var properties = await parallelContext.AuthUserProperties.SingleAsync(x => x.UserId == UserId);
            properties.OtpEnabled = true;
            await parallelContext.SaveChangesAsync();
        }

        continueRequest.TrySetResult(true);
        var exception = await Assert.ThrowsAsync<OtpCodeNeedException>(() => operation);
        exception.ErrorMetadata["x-otp-method"].Should().Be("authenticator");
    }

    private Mock<IAuthPropertiesStorage> CreateBarrierStorage(TaskCompletionSource<bool> initialRead,
        TaskCompletionSource<bool> continueRequest)
    {
        var storage = new AuthPropertiesStorage(_db.Context);
        var mock = new Mock<IAuthPropertiesStorage>();
        var reads = 0;
        mock.Setup(s => s.TryReserveReauthPasswordAttempt(UserId, It.IsAny<CancellationToken>()))
            .Returns((long userId, CancellationToken cancellationToken) =>
                storage.TryReserveReauthPasswordAttempt(userId, cancellationToken));
        mock.Setup(s => s.ResetReauthPasswordAttempts(UserId, It.IsAny<CancellationToken>()))
            .Returns((long userId, CancellationToken cancellationToken) =>
                storage.ResetReauthPasswordAttempts(userId, cancellationToken));
        mock.Setup(s => s.GetUserAuthProperties(UserId, It.IsAny<CancellationToken>()))
            .Returns(async (long userId, CancellationToken cancellationToken) =>
            {
                var properties = await storage.GetUserAuthProperties(userId, cancellationToken);
                if (Interlocked.Increment(ref reads) == 1)
                {
                    initialRead.TrySetResult(true);
                    await continueRequest.Task.WaitAsync(cancellationToken);
                }

                return properties;
            });
        mock.Setup(s => s.GetUserAuthPropertiesSnapshot(UserId, It.IsAny<CancellationToken>()))
            .Returns((long userId, CancellationToken cancellationToken) =>
                storage.GetUserAuthPropertiesSnapshot(userId, cancellationToken));
        return mock;
    }

    private (BeginReauthenticationCommandHandler Handler, Mock<INotificationOutbox> Outbox) CreateHandler(
        IAuthPropertiesStorage authPropertiesStorage)
    {
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

    private async Task SeedUserAsync(AuthUserProperty properties)
    {
        _db.Context.AuthUserProperties.Add(properties);
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
