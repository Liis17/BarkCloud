using System.Collections.Concurrent;
using System.Text.Json;

using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using MassTransit;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public class NotificationOutboxTests
{
    [Fact]
    public async Task TwoWorkersOnSharedRows_EachNotificationIsDeliveredExactlyOnce()
    {
        const int total = 60;
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            for (var i = 0; i < total; i++)
            {
                context.PendingNotifications.Add(Row(new Dictionary<string, string> { ["n"] = i.ToString() }));
            }

            await context.SaveChangesAsync();
        }

        var published = new ConcurrentBag<string>();
        using var first = Host(database, published);
        using var second = Host(database, published);

        // Воркеры гонятся за одними и теми же строками: захват через условный UPDATE не должен дать дублей.
        await Task.WhenAll(Drain(first.Worker), Drain(second.Worker));

        published.Should().HaveCount(total);
        published.Distinct().Should().HaveCount(total);
        await using var reader = database.CreateContext();
        (await reader.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RowLeasedByCrashedWorker_IsReclaimedAfterLeaseExpires()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var row = Row(new Dictionary<string, string> { ["n"] = "1" });
            row.Attempts = 1;
            row.LockToken = Guid.NewGuid();
            row.LockedUntil = DateTime.UtcNow.AddMinutes(1);
            context.PendingNotifications.Add(row);
            await context.SaveChangesAsync();
        }

        var published = new ConcurrentBag<string>();
        using var host = Host(database, published);

        (await host.Worker.ProcessBatchAsync(default)).Should().Be(0);

        await using (var context = database.CreateContext())
        {
            await context.PendingNotifications.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddSeconds(-1)));
        }

        (await host.Worker.ProcessBatchAsync(default)).Should().Be(1);
        published.Should().ContainSingle();
    }

    [Fact]
    public async Task UsersDownThenRecovered_NotificationIsDeliveredLater()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            await new NotificationOutbox(context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                    new MetricsCollector(), NullLogger<NotificationOutbox>.Instance)
                .EnqueueAsync(42, NotificationType.PasswordChanged, "Пароль успешно изменен",
                    new Dictionary<string, string> { ["n"] = "1" });
        }

        var published = new ConcurrentBag<string>();
        using var host = Host(database, published, usersAvailable: false);

        await host.Worker.ProcessBatchAsync(default);

        published.Should().BeEmpty();
        await using (var reader = database.CreateContext())
        {
            var pending = await reader.PendingNotifications.SingleAsync();
            pending.Attempts.Should().Be(1);
            pending.NextAttemptAt.Should().BeAfter(DateTime.UtcNow);

            await reader.PendingNotifications.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        host.UsersAvailable = true;
        await host.Worker.ProcessBatchAsync(default);

        published.Should().ContainSingle();
        await using var after = database.CreateContext();
        (await after.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPassword_UsersDownAfterPasswordWrite_ClientSucceedsAndMailIsDeliveredLater()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
        }

        var published = new ConcurrentBag<string>();
        using var host = Host(database, published, usersAvailable: false);

        // Users недоступен весь вызов: раньше смена пароля падала на запросе контактов уже после записи хеша.
        await using (var context = database.CreateContext())
        {
            var metrics = new MetricsCollector();
            var outbox = new NotificationOutbox(context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                metrics, NullLogger<NotificationOutbox>.Instance);
            var handler = new SetPasswordCommandHandler(
                UserContextFor(42), new PasswordsStorage(context), new AuthPropertiesStorage(context),
                new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
                new PasswordChangedNotifier(outbox, new RequestContext { DeviceName = "Phone", IpAddress = "1.1.1.1" }),
                metrics, NullLogger<SetPasswordCommandHandler>.Instance, context);

            await handler.Handle(new SetPasswordCommand { OldPassword = "old-password", NewPassword = "new-password" }, default);
        }

        await using (var reader = database.CreateContext())
        {
            PasswordHasher.VerifyPassword("new-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
                .Should().BeTrue();
            (await reader.PendingNotifications.SingleAsync()).Type.Should().Be(NotificationType.PasswordChanged);
        }

        await host.Worker.ProcessBatchAsync(default);
        published.Should().BeEmpty();

        await using (var context = database.CreateContext())
        {
            await context.PendingNotifications.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        host.UsersAvailable = true;
        await host.Worker.ProcessBatchAsync(default);

        published.Should().ContainSingle();
        await using var after = database.CreateContext();
        (await after.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Migration_DownAndUp_KeepsOtherIdentityData()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await new PasswordsStorage(context).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
        context.PendingNotifications.Add(Row(new Dictionary<string, string>()));
        await context.SaveChangesAsync();
        // Откат миграции outbox: берём предшествующую ей, а не предпоследнюю — после неё добавляются другие миграции.
        var migrations = context.Database.GetMigrations().ToList();
        var previous = migrations[migrations.FindIndex(x => x.EndsWith("_AddPendingNotifications")) - 1];

        await context.GetService<IMigrator>().MigrateAsync(previous);
        await context.Database.MigrateAsync();

        await using var reader = database.CreateContext();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(reader).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await reader.PendingNotifications.AnyAsync()).Should().BeFalse();
        reader.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static UserContext UserContextFor(long userId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(IdentityClaims.UserId, userId.ToString()),
            new Claim(IdentityClaims.TokenType, TokenType.User.ToString()),
            new Claim(IdentityClaims.DeviceId, "device-1")
        ], authenticationType: "test");

        return new UserContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        });
    }

    private static async Task Drain(NotificationOutboxWorker worker)
    {
        while (await worker.ProcessBatchAsync(default) > 0)
        {
        }
    }

    private static PendingNotification Row(Dictionary<string, string> payload) => new()
    {
        Id = Guid.NewGuid(),
        UserId = 42,
        Type = NotificationType.PasswordChanged,
        Title = "Пароль успешно изменен",
        PayloadJson = JsonSerializer.Serialize(payload),
        CreatedAt = DateTime.UtcNow,
        NextAttemptAt = DateTime.UtcNow.AddSeconds(-1)
    };

    private static WorkerHost Host(PostgresIdentityDatabase database, ConcurrentBag<string> published, bool usersAvailable = true)
        => new(database, published, usersAvailable);

    private sealed class WorkerHost : IDisposable
    {
        private readonly ServiceProvider _services;

        public WorkerHost(PostgresIdentityDatabase database, ConcurrentBag<string> published, bool usersAvailable)
        {
            UsersAvailable = usersAvailable;

            var users = new Mock<UsersServerApi.UsersServerApiClient>();
            users.Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns(() => UsersAvailable
                    ? GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
                    {
                        User = new User { Id = 42, Username = "user42" },
                        Contact = new UserContact { Email = "u@e" }
                    })
                    : throw new RpcException(new Status(StatusCode.Unavailable, "users down")));

            var sender = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            sender.Setup(s => s.SendNotification(It.IsAny<Notification>()))
                .Callback<Notification>(n => published.Add(n.Payload.GetValueOrDefault("n") ?? Guid.NewGuid().ToString()))
                .Returns(Task.CompletedTask);

            var location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);

            _services = new ServiceCollection()
                .AddScoped<IdentityContext>(_ => database.CreateContext())
                .AddSingleton(users.Object)
                .AddScoped(_ => sender.Object)
                .AddScoped(_ => location.Object)
                .BuildServiceProvider();

            Worker = new NotificationOutboxWorker(
                _services.GetRequiredService<IServiceScopeFactory>(), new MetricsCollector(), NullLogger<NotificationOutboxWorker>.Instance);
        }

        public bool UsersAvailable { get; set; }

        public NotificationOutboxWorker Worker { get; }

        public void Dispose() => _services.Dispose();
    }
}
