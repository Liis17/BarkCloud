using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Consumers;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public class UserDeletedTests
{
    [Fact]
    public async Task UserDeleted_WhenCleanupFails_PreservesPasswordAndSessions()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var tokens = new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 });
        await tokens.CreateNewRefreshToken("old-refresh", 42, "phone", 1);
        await new PasswordsStorage(context).UpdateUserPasswordHash(42, "old-hash");
        context.AuthUserProperties.Add(new AuthUserProperty { UserId = 42 });
        var reset = new ResetPassword
        {
            Id = Guid.NewGuid(), UserId = 42, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var code = new ConfirmationCode
        {
            Id = Guid.NewGuid(), OwnerId = 42, Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
        };
        context.ResetPasswords.Add(reset);
        context.ConfirmationCodes.Add(code);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_auth_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'cleanup write failed'; END $$;
            CREATE TRIGGER reject_auth_cleanup BEFORE DELETE ON "AuthUserProperties"
            FOR EACH ROW EXECUTE FUNCTION reject_auth_cleanup();
            """);
        var handler = CreateConsumer(context);
        var message = new Mock<ConsumeContext<UserDeleted>>();
        message.SetupGet(c => c.Message).Returns(new UserDeleted { UserId = 42 });

        await FluentActions.Awaiting(() => handler.Consume(message.Object)).Should().ThrowAsync<PostgresException>();

        await using var reader = database.CreateContext();
        (await new PasswordsStorage(reader).GetUserPasswordHash(42)).Should().Be("old-hash");
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42))
            .Should().ContainSingle(t => t.Value == "old-refresh");
        (await reader.RevokedSessions.AnyAsync()).Should().BeFalse();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id)).Should().NotBeNull();
        (await new ConfirmationCodesStorage(reader).GetCode(code.Id)).Should().NotBeNull();

        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_auth_cleanup ON \"AuthUserProperties\";");
        await using var retry = database.CreateContext();
        await CreateConsumer(retry).Consume(message.Object);
        reader.ChangeTracker.Clear();
        (await new PasswordsStorage(reader).GetUserPasswordHash(42)).Should().BeNull();
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42)).Should().BeEmpty();
        (await reader.AuthUserProperties.AnyAsync()).Should().BeFalse();
        (await new ResetPasswordsStorage(reader).GetResetPassword(reset.Id)).Should().BeNull();
        (await new ConfirmationCodesStorage(reader).GetCode(code.Id)).Should().BeNull();
        (await reader.RevokedSessions.SingleAsync()).DeviceId.Should().Be("phone");
    }

    [Fact]
    public async Task UserDeleted_Redelivered_PreservesOriginalDurableRevocations()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var first = database.CreateContext();
        var tokens = new RefreshTokensStorage(first, new JwtSettings { ExpiryMinutes = 60 });
        await tokens.CreateNewRefreshToken("phone-refresh", 42, "phone", 1);
        await tokens.CreateNewRefreshToken("desktop-refresh", 42, "desktop", 1);
        var message = new Mock<ConsumeContext<UserDeleted>>();
        message.SetupGet(c => c.Message).Returns(new UserDeleted { UserId = 42 });

        await CreateConsumer(first).Consume(message.Object);
        await using var reader = database.CreateContext();
        var original = await reader.RevokedSessions.AsNoTracking().ToArrayAsync();
        original.Select(x => x.DeviceId).Should().BeEquivalentTo("phone", "desktop");
        await using var retry = database.CreateContext();
        await CreateConsumer(retry).Consume(message.Object);

        (await reader.RevokedSessions.AsNoTracking().ToArrayAsync()).Should().BeEquivalentTo(original);
        (await new RefreshTokensStorage(reader, new JwtSettings()).GetRefreshTokens(42)).Should().BeEmpty();
    }

    private static UserDeletedConsumer CreateConsumer(IdentityContext context) => new(
        new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }), new PasswordsStorage(context),
        new AuthPropertiesStorage(context), new ResetPasswordsStorage(context), new ConfirmationCodesStorage(context),
        new MetricsCollector(), NullLogger<UserDeletedConsumer>.Instance, context);
}
