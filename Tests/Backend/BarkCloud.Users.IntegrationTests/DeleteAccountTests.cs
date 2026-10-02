using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Users;
using BarkCloud.Users.Features.DeleteAccount;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MassTransit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.IntegrationTests;

public class DeleteAccountTests
{
    [Fact]
    public async Task DeleteAccount_WhenEventCannotBeSaved_PreservesUserAndDevices()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var user = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await storage.GetOrCreatePrivacy(user.Id);
        await new DevicesStorage(context).RegisterOrUpdateDevice(Guid.NewGuid(), user.Id, "Phone", null, null, null);
        var publish = new Mock<IPublishEndpoint>();
        publish.Setup(p => p.Publish(It.IsAny<UserDeleted>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("event write failed"));
        var metrics = new MetricsCollector();
        var handler = new DeleteAccountCommandHandler(storage, CreateUserContext(user.Id),
            new UserInfoQueueSender(publish.Object, metrics, context), metrics,
            NullLogger<DeleteAccountCommandHandler>.Instance, context);

        await FluentActions.Awaiting(() => handler.Handle(new DeleteAccountCommand(), default))
            .Should().ThrowAsync<InvalidOperationException>();

        await using var reader = database.CreateContext();
        (await new UsersStorage(reader).GetById(user.Id)).Should().NotBeNull();
        (await new DevicesStorage(reader).GetDevicesByUserId(user.Id)).Should().ContainSingle();
        metrics.SnapshotAndReset().Should().NotContainKey("accounts_deleted");
    }

    internal static UserContext CreateUserContext(long userId) => new(new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(IdentityClaims.UserId, userId.ToString()),
                new Claim(IdentityClaims.TokenType, TokenType.User.ToString())
            ], "test"))
        }
    });
}
