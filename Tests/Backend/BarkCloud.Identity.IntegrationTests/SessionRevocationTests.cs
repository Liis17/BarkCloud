using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Features.Logout;
using BarkCloud.Identity.Features.RemoveActiveSession;
using BarkCloud.Identity.Features.RemoveActiveSessionServer;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public class SessionRevocationTests
{
    [Theory]
    [InlineData("logout")]
    [InlineData("remove")]
    [InlineData("server")]
    public async Task Revoke_WhenDurableRecordCannotBeSaved_PreservesRefreshTokens(string operation)
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 });
        await storage.CreateNewRefreshToken("phone-refresh", 42, "phone", 1);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_revocation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'revocation write failed'; END $$;
            CREATE TRIGGER reject_revocation BEFORE INSERT ON "RevokedSessions"
            FOR EACH ROW EXECUTE FUNCTION reject_revocation();
            """);

        await FluentActions.Awaiting(() => Run(operation, storage)).Should().ThrowAsync<DbUpdateException>();

        await using var reader = database.CreateContext();
        (await new RefreshTokensStorage(reader, new JwtSettings()).FindRefreshToken("phone-refresh")).Should().NotBeNull();
        (await reader.RevokedSessions.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("remove")]
    [InlineData("server")]
    public async Task Revoke_UsersServiceUnavailable_KeepsCommittedRevocation(string operation)
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 });
        await storage.CreateNewRefreshToken("phone-refresh", 42, "phone", 1);
        await storage.CreateNewRefreshToken("other-refresh", 42, "other", 1);

        await Run(operation, storage);

        await using var reader = database.CreateContext();
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42))
            .Should().ContainSingle().Which.Value.Should().Be("other-refresh");
        var revoked = await reader.RevokedSessions.SingleAsync();
        revoked.UserId.Should().Be(42);
        revoked.DeviceId.Should().Be("phone");
    }

    [Fact]
    public async Task Logout_RefreshAlreadyMissing_StillCreatesDurableAccessRevocation()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await Run("logout", new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }));

        await using var reader = database.CreateContext();
        (await reader.RevokedSessions.SingleAsync()).DeviceId.Should().Be("phone");
    }

    private static Task Run(string operation, IRefreshTokensStorage storage)
    {
        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Throws(new InvalidOperationException("Users unavailable"));
        var metrics = new MetricsCollector();
        var userContext = new UserContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(IdentityClaims.UserId, "42"),
                    new Claim(IdentityClaims.TokenType, TokenType.User.ToString()),
                    new Claim(IdentityClaims.DeviceId, "phone")
                ], "test"))
            }
        });
        return operation switch
        {
            "logout" => new LogoutCommandHandler(storage, userContext, users.Object, metrics,
                NullLogger<LogoutCommandHandler>.Instance).Handle(new LogoutCommand(), default),
            "remove" => new RemoveActiveSessionCommandHandler(storage, userContext, users.Object, metrics,
                NullLogger<RemoveActiveSessionCommandHandler>.Instance)
                .Handle(new RemoveActiveSessionCommand { DeviceId = "phone" }, default),
            "server" => new RemoveActiveSessionServerCommandHandler(storage, users.Object, metrics,
                NullLogger<RemoveActiveSessionServerCommandHandler>.Instance)
                .Handle(new RemoveActiveSessionServerCommand { UserId = 42, DeviceId = "phone" }, default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}
