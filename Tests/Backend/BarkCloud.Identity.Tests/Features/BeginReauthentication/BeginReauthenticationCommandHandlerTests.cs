using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.BeginReauthentication;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BarkCloud.Identity.Tests.Features.BeginReauthentication;

public sealed class BeginReauthenticationCommandHandlerTests : IDisposable
{
    private const long UserId = 7;
    private const long SessionId = 55;
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SetInitialPassword_CannotUsePasswordReauthentication()
    {
        _db.Context.RefreshTokens.Add(new RefreshToken
        {
            Id = SessionId,
            UserId = UserId,
            Value = "refresh",
            DeviceId = "device-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await _db.Context.SaveChangesAsync();

        var authProperties = new Mock<IAuthPropertiesStorage>();
        var passwordsStorage = new PasswordsStorage(_db.Context);
        var handler = new BeginReauthenticationCommandHandler(
            CreateUserContext(),
            _db.Context,
            authProperties.Object,
            passwordsStorage,
            new ReauthPasswordVerifier(authProperties.Object, passwordsStorage),
            Mock.Of<IWebAuthnStorage>(),
            new ReauthenticationService(_db.Context, passwordsStorage),
            Mock.Of<IFido2>(),
            null!,
            Mock.Of<INotificationOutbox>(),
            Mock.Of<IAuthRateLimiter>(),
            new MetricsCollector(),
            new RequestContext(),
            NullLogger<BeginReauthenticationCommandHandler>.Instance);

        var act = () => handler.Handle(new BeginReauthenticationCommand
        {
            Action = ReauthenticationAction.SetInitialPassword,
            Password = "not-a-factor"
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ReauthenticationRequiredException>();
        authProperties.VerifyNoOtherCalls();
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
