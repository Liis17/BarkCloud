using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Services;

public class NotificationOutboxWorkerTests : IDisposable
{
    private readonly SqliteIdentityContext _database = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _users = new();
    private readonly Mock<NotificationQueueSender> _sender;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ServiceProvider _services;
    private readonly List<EmailNotification> _published = [];

    public NotificationOutboxWorkerTests()
    {
        _sender = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _sender.Setup(s => s.SendNotification(It.IsAny<Notification>()))
            .Callback<Notification>(n => _published.Add((EmailNotification)n))
            .Returns(Task.CompletedTask);

        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>()))
            .ReturnsAsync(new IpLocation { Country = "Россия", RegionName = "Москва", City = "Москва" });

        UsersReturnsContact("u@e");

        _services = new ServiceCollection()
            .AddScoped<IdentityContext>(_ => _database.CreateAdditionalContext())
            .AddSingleton(_users.Object)
            .AddScoped(_ => _sender.Object)
            .AddScoped(_ => _location.Object)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
    }

    private NotificationOutboxWorker CreateSut() => new(
        _services.GetRequiredService<IServiceScopeFactory>(), _metrics, NullLogger<NotificationOutboxWorker>.Instance);

    private void UsersReturnsContact(string email)
        => _users
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "user42" },
                Contact = new UserContact { Email = email }
            }));

    private void UsersFails()
        => _users
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "users down")));

    private async Task<PendingNotification> Seed(
        Dictionary<string, string>? payload = null,
        Action<PendingNotification>? configure = null)
    {
        var row = new PendingNotification
        {
            Id = Guid.NewGuid(),
            UserId = 42,
            Type = NotificationType.PasswordChanged,
            Title = "Пароль успешно изменен",
            PayloadJson = JsonSerializer.Serialize(payload ?? new Dictionary<string, string> { ["ip"] = "1.1.1.1", ["datetime"] = "01.01.2026 10:00:00" }),
            CreatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1)
        };
        configure?.Invoke(row);

        await using var context = _database.CreateAdditionalContext();
        context.PendingNotifications.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    private async Task<PendingNotification?> Reload(Guid id)
    {
        await using var context = _database.CreateAdditionalContext();
        return await context.PendingNotifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    [Fact]
    public async Task ProcessBatch_Success_PublishesEmailWithContactsAndLocationAndDeletesRow()
    {
        var row = await Seed();

        var claimed = await CreateSut().ProcessBatchAsync(default);

        claimed.Should().Be(1);
        var email = _published.Should().ContainSingle().Subject;
        email.Address.Should().Be("u@e");
        email.OwnerId.Should().Be(42);
        email.Type.Should().Be(NotificationType.PasswordChanged);
        email.Title.Should().Be("Пароль успешно изменен");
        email.Payload.Should().Contain("username", "user42")
            .And.Contain("location", "Россия, Москва, Москва")
            .And.Contain("ip", "1.1.1.1")
            .And.Contain("datetime", "01.01.2026 10:00:00");
        (await Reload(row.Id)).Should().BeNull();
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_sent");
    }

    [Fact]
    public async Task ProcessBatch_PayloadAlreadyHasLocation_DoesNotRequestIt()
    {
        await Seed(new Dictionary<string, string> { ["ip"] = "1.1.1.1", ["location"] = "Известно" });

        await CreateSut().ProcessBatchAsync(default);

        _published.Single().Payload["location"].Should().Be("Известно");
        _location.Verify(c => c.GetLocation(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatch_PayloadWithoutIp_AddsNoLocation()
    {
        await Seed(new Dictionary<string, string> { ["adminusername"] = "AdminPanel" });

        await CreateSut().ProcessBatchAsync(default);

        _published.Single().Payload.Should().NotContainKey("location").And.Contain("username", "user42");
        _location.Verify(c => c.GetLocation(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatch_UsersUnavailable_KeepsRowForLaterAndDeliversAfterRecovery()
    {
        var row = await Seed();
        UsersFails();

        await CreateSut().ProcessBatchAsync(default);

        var pending = (await Reload(row.Id))!;
        pending.Attempts.Should().Be(1);
        pending.NextAttemptAt.Should().BeAfter(DateTime.UtcNow);
        pending.LockedUntil.Should().BeNull();
        pending.LockToken.Should().BeNull();
        _published.Should().BeEmpty();
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_failed");

        // Users поднялся, пауза прошла.
        UsersReturnsContact("u@e");
        await using (var context = _database.CreateAdditionalContext())
        {
            await context.PendingNotifications.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        await CreateSut().ProcessBatchAsync(default);

        _published.Should().ContainSingle();
        (await Reload(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatch_PublishFails_KeepsRowAndGrowsBackoff()
    {
        var row = await Seed();
        _sender.Setup(s => s.SendNotification(It.IsAny<Notification>())).ThrowsAsync(new InvalidOperationException("rabbit down"));

        await CreateSut().ProcessBatchAsync(default);
        var first = (await Reload(row.Id))!;

        await using (var context = _database.CreateAdditionalContext())
        {
            await context.PendingNotifications.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        await CreateSut().ProcessBatchAsync(default);
        var second = (await Reload(row.Id))!;

        first.Attempts.Should().Be(1);
        first.NextAttemptAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(10));
        second.Attempts.Should().Be(2);
        second.NextAttemptAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ProcessBatch_NotYetDue_IsNotClaimed()
    {
        var row = await Seed(configure: r => r.NextAttemptAt = DateTime.UtcNow.AddMinutes(5));

        var claimed = await CreateSut().ProcessBatchAsync(default);

        claimed.Should().Be(0);
        (await Reload(row.Id))!.Attempts.Should().Be(0);
        _users.Verify(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatch_ActiveLeaseOfAnotherWorker_IsNotClaimed()
    {
        await Seed(configure: r =>
        {
            r.LockToken = Guid.NewGuid();
            r.LockedUntil = DateTime.UtcNow.AddMinutes(1);
        });

        var claimed = await CreateSut().ProcessBatchAsync(default);

        claimed.Should().Be(0);
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessBatch_ExpiredLease_IsClaimedAgain()
    {
        var row = await Seed(configure: r =>
        {
            r.Attempts = 1;
            r.LockToken = Guid.NewGuid();
            r.LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        });

        var claimed = await CreateSut().ProcessBatchAsync(default);

        claimed.Should().Be(1);
        _published.Should().ContainSingle();
        (await Reload(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatch_OlderThanMaxAge_IsDroppedWithoutSending()
    {
        var row = await Seed(configure: r => r.CreatedAt = DateTime.UtcNow.AddHours(-25));

        await CreateSut().ProcessBatchAsync(default);

        _published.Should().BeEmpty();
        (await Reload(row.Id)).Should().BeNull();
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_expired");
    }

    [Fact]
    public async Task ProcessBatch_UserWithoutEmail_IsDroppedWithoutSending()
    {
        var row = await Seed();
        UsersReturnsContact(string.Empty);

        await CreateSut().ProcessBatchAsync(default);

        _published.Should().BeEmpty();
        (await Reload(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatch_TakesAtMostOneBatchOrderedByDueTime()
    {
        for (var i = 0; i < 25; i++)
        {
            var offset = i;
            await Seed(configure: r => r.NextAttemptAt = DateTime.UtcNow.AddMinutes(-100 + offset));
        }

        var claimed = await CreateSut().ProcessBatchAsync(default);

        claimed.Should().Be(20);
        await using var context = _database.CreateAdditionalContext();
        (await context.PendingNotifications.CountAsync()).Should().Be(5);
    }
}
