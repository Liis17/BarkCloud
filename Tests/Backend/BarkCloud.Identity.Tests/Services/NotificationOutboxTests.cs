using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Services;

public class NotificationOutboxTests : IDisposable
{
    private readonly SqliteIdentityContext _database = new();
    private readonly MetricsCollector _metrics = new();

    public void Dispose() => _database.Dispose();

    private NotificationOutbox CreateSut(bool emailEnabled = true) => new(
        _database.Context,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = emailEnabled.ToString() })
            .Build(),
        _metrics,
        NullLogger<NotificationOutbox>.Instance);

    [Fact]
    public async Task EnqueueAsync_StoresDueRowWithPayload()
    {
        var enqueued = await CreateSut().EnqueueAsync(
            42, NotificationType.PasswordChanged, "Пароль успешно изменен", new Dictionary<string, string> { ["ip"] = "1.1.1.1" });

        enqueued.Should().BeTrue();

        await using var reader = _database.CreateAdditionalContext();
        var row = await reader.PendingNotifications.SingleAsync();
        row.UserId.Should().Be(42);
        row.Type.Should().Be(NotificationType.PasswordChanged);
        row.Title.Should().Be("Пароль успешно изменен");
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.PayloadJson)
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["ip"] = "1.1.1.1" });
        row.Attempts.Should().Be(0);
        row.NextAttemptAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        row.LockToken.Should().BeNull();
        _metrics.SnapshotAndReset().Should().NotContainKey("notification_outbox_enqueued");
    }

    [Fact]
    public async Task EnqueueAsync_EmailDisabled_StoresNothing()
    {
        var enqueued = await CreateSut(emailEnabled: false).EnqueueAsync(
            42, NotificationType.PasswordChanged, "t", new Dictionary<string, string>());

        enqueued.Should().BeFalse();
        await using var reader = _database.CreateAdditionalContext();
        (await reader.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueAsync_SaveFails_ThrowsAndDetachesEntity()
    {
        await _database.Context.Database.ExecuteSqlRawAsync("DROP TABLE PendingNotifications");

        var act = () => CreateSut().EnqueueAsync(
            42, NotificationType.PasswordChanged, "t", new Dictionary<string, string>());

        await act.Should().ThrowAsync<DbUpdateException>();
        _database.Context.ChangeTracker.Entries().Should().BeEmpty();
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_enqueue_failed");
    }

    [Fact]
    public void Device_FromRequestContext_ContainsDeviceBlockWithoutLocation()
    {
        var context = new RequestContext
        {
            IpAddress = "1.1.1.1",
            DeviceName = "Pixel",
            OperationSystem = "Android 14",
            AppName = "BarkCloud",
            AppVersion = "1.0"
        };

        var payload = NotificationPayload.Device(context);

        payload.Should().Contain("ip", "1.1.1.1")
            .And.Contain("devicename", "Pixel")
            .And.Contain("os", "Android 14")
            .And.Contain("appname", "BarkCloud v.1.0")
            .And.ContainKey("datetime");
        payload.Should().NotContainKey("location");
    }

    [Fact]
    public void Device_WithLocation_KeepsKnownLocation()
    {
        var payload = NotificationPayload.Device(new RequestContext(), "Россия, Москва, Москва");

        payload["location"].Should().Be("Россия, Москва, Москва");
    }
}
