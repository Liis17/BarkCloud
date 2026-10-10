using System.Reflection;

using BarkCloud.Proto.Torrent;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;
using BarkCloud.Torrent.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using MonoTorrent.Client;

namespace BarkCloud.Torrent.Tests.Infrastructure;

public class TorrentPersistenceServiceTests
{
    [Fact]
    public async Task FlushAsync_WhenSqliteUpdateAborts_RetrySavesTheWholeTrafficDelta()
    {
        await using var fixture = await Fixture.CreateAsync();
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        await fixture.Database.ExecuteSqlAsync("""
            CREATE TRIGGER RejectTorrentUpdate BEFORE UPDATE ON Torrents
            BEGIN SELECT RAISE(ABORT, 'torrent update rejected'); END;
            """);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();

        await AssertTotalsAsync(fixture, (managed.Id, 100, 200));
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);
        managed.BaselineFor(100, 200).Should().Be((0, 0));

        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER RejectTorrentUpdate;");
        fixture.Service.SetTraffic(managed.Id, 15, 27);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 115, 227));
        managed.LastSessionDownloaded.Should().Be(15);
        managed.LastSessionUploaded.Should().Be(27);
    }

    [Fact]
    public async Task FlushAsync_RepeatingTheSameSnapshot_DoesNotAddTrafficAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);

        await fixture.Service.FlushAsync(default);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 110, 220));
        managed.LastSessionDownloaded.Should().Be(10);
        managed.LastSessionUploaded.Should().Be(20);
    }

    [Fact]
    public async Task FlushAsync_TrafficArrivingDuringSave_IsSavedOnTheNextTick()
    {
        var afterSave = new SaveChangesCallbackInterceptor();
        await using var fixture = await Fixture.CreateAsync(afterSave);
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        afterSave.OnSaved = () => fixture.Service.SetTraffic(managed.Id, 15, 27);

        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 110, 220));
        managed.LastSessionDownloaded.Should().Be(10);
        managed.LastSessionUploaded.Should().Be(20);

        afterSave.OnSaved = null;
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 115, 227));
    }

    [Fact]
    public async Task FlushAsync_WhenCancelledBeforeWrite_KeepsBaselineAndRetriesAllTraffic()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelBeforeSave = new CancelBeforeSaveInterceptor(cancellation);
        await using var fixture = await Fixture.CreateAsync(cancelBeforeSave);
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        await AssertTotalsAsync(fixture, (managed.Id, 100, 200));
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);
        managed.BaselineFor(100, 200).Should().Be((0, 0));

        cancelBeforeSave.Enabled = false;
        fixture.Service.SetTraffic(managed.Id, 15, 27);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 115, 227));
    }

    [Fact]
    public async Task FlushAsync_WhenSecondBatchUpdateAborts_RollsBackAndRetriesEveryTorrent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        var second = await fixture.AddDistinctTorrentAsync(downloaded: 300, uploaded: 400);
        fixture.Service.SetTraffic(first.Id, 10, 20);
        fixture.Service.SetTraffic(second.Id, 30, 40);
        await fixture.Database.ExecuteSqlAsync("""
            CREATE TABLE TorrentUpdateCounter (Value INTEGER NOT NULL);
            INSERT INTO TorrentUpdateCounter VALUES (0);
            CREATE TRIGGER CountTorrentUpdate AFTER UPDATE ON Torrents
            BEGIN UPDATE TorrentUpdateCounter SET Value = Value + 1; END;
            CREATE TRIGGER RejectSecondTorrentUpdate BEFORE UPDATE ON Torrents
            WHEN (SELECT Value FROM TorrentUpdateCounter) = 1
            BEGIN SELECT RAISE(ABORT, 'second torrent update rejected'); END;
            """);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();

        await AssertTotalsAsync(fixture, (first.Id, 100, 200), (second.Id, 300, 400));
        first.LastSessionDownloaded.Should().Be(0);
        first.LastSessionUploaded.Should().Be(0);
        second.LastSessionDownloaded.Should().Be(0);
        second.LastSessionUploaded.Should().Be(0);

        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER RejectSecondTorrentUpdate;");
        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER CountTorrentUpdate;");
        await fixture.Database.ExecuteSqlAsync("DROP TABLE TorrentUpdateCounter;");
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (first.Id, 110, 220), (second.Id, 330, 440));
    }

    [Fact]
    public async Task FlushAsync_WhenReadingSecondTorrentFails_DoesNotChangeFirstBaseline()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddTorrentAsync(downloaded: 50, uploaded: 60);
        var second = await fixture.AddDistinctTorrentAsync(downloaded: 70, uploaded: 80);
        fixture.Service.SetTraffic(first.Id, 10, 20);
        fixture.Service.SetTraffic(second.Id, 30, 40);
        fixture.Service.ThrowOnReadNumber = 2;

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<IOException>();

        fixture.Service.Reads.Should().HaveCount(2);
        fixture.Service.Reads.Should().Contain(first.Id);
        fixture.Service.Reads.Should().Contain(second.Id);
        fixture.Service.Reads[0].Should().NotBe(fixture.Service.Reads[1]);
        first.LastSessionDownloaded.Should().Be(0);
        first.LastSessionUploaded.Should().Be(0);
        first.PendingFlush.Should().BeNull();
        second.LastSessionDownloaded.Should().Be(0);
        second.LastSessionUploaded.Should().Be(0);
        second.PendingFlush.Should().BeNull();
        await AssertTotalsAsync(fixture, (first.Id, 50, 60), (second.Id, 70, 80));

        fixture.Service.ThrowOnReadNumber = null;
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (first.Id, 60, 80), (second.Id, 100, 120));
    }

    [Fact]
    public async Task FlushAsync_WhenTorrentRowIsMissing_SkipsItsBaselineAndSavesOtherRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.AddTorrentAsync(downloaded: 5, uploaded: 6);
        var missing = await fixture.AddDistinctManagedTorrentAsync();
        fixture.Service.SetTraffic(saved.Id, 10, 20);
        fixture.Service.SetTraffic(missing.Id, 30, 40);

        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (saved.Id, 15, 26));
        saved.LastSessionDownloaded.Should().Be(10);
        saved.LastSessionUploaded.Should().Be(20);
        missing.LastSessionDownloaded.Should().Be(0);
        missing.LastSessionUploaded.Should().Be(0);
        missing.PendingFlush.Should().BeNull();
        fixture.Service.Reads.Should().NotContain(missing.Id);
    }

    [Fact]
    public async Task FlushAsync_WhenCommitConfirmationIsLost_MapperAndNextTickUseCheckpoint()
    {
        var afterSave = new ThrowAfterSaveInterceptor();
        await using var fixture = await Fixture.CreateAsync(afterSave);
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        afterSave.Failure = new IOException("confirmation lost");

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<IOException>();

        await AssertTotalsAsync(fixture, (managed.Id, 110, 220));
        managed.PendingFlush.Should().NotBeNull();
        managed.BaselineFor(110, 220).Should().Be((10, 20));

        var stored = (await fixture.Database.ReadAsync(managed.Id))!;
        SetMonitorCounter(managed.Manager.Monitor, nameof(ConnectionMonitor.DataBytesReceived), 10);
        SetMonitorCounter(managed.Manager.Monitor, nameof(ConnectionMonitor.DataBytesSent), 20);
        var info = TorrentMapper.ToInfo(stored, managed);
        info.Downloaded.Should().Be(110);
        info.Uploaded.Should().Be(220);

        await fixture.Service.FlushAsync(default);
        await AssertTotalsAsync(fixture, (managed.Id, 110, 220));
        managed.LastSessionDownloaded.Should().Be(10);
        managed.LastSessionUploaded.Should().Be(20);
        managed.PendingFlush.Should().BeNull();

        fixture.Service.SetTraffic(managed.Id, 15, 27);
        await fixture.Service.FlushAsync(default);
        await AssertTotalsAsync(fixture, (managed.Id, 115, 227));
    }

    [Fact]
    public async Task FlushAsync_AfterSeveralAborts_SavesFullDifferenceOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        await fixture.Database.ExecuteSqlAsync("""
            CREATE TRIGGER RejectTorrentUpdate BEFORE UPDATE ON Torrents
            BEGIN SELECT RAISE(ABORT, 'torrent update rejected'); END;
            """);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();
        fixture.Service.SetTraffic(managed.Id, 15, 27);
        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();
        await AssertTotalsAsync(fixture, (managed.Id, 100, 200));
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);

        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER RejectTorrentUpdate;");
        await fixture.Service.FlushAsync(default);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 115, 227));
    }

    [Fact]
    public async Task FlushAsync_WhenResolvingCheckpointThenBatchAborts_DoesNotLoseOrDuplicateTraffic()
    {
        var afterSave = new ThrowAfterSaveInterceptor();
        await using var fixture = await Fixture.CreateAsync(afterSave);
        var previouslyCommitted = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        var other = await fixture.AddDistinctManagedTorrentAsync();
        fixture.Service.SetTraffic(previouslyCommitted.Id, 10, 20);
        afterSave.Failure = new IOException("confirmation lost");

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<IOException>();
        await AssertTotalsAsync(fixture, (previouslyCommitted.Id, 110, 220));

        await fixture.Database.SeedAsync(Fixture.CreateEntity(other, 300, 400));
        fixture.Service.SetTraffic(previouslyCommitted.Id, 15, 27);
        fixture.Service.SetTraffic(other.Id, 5, 7);
        await fixture.Database.ExecuteSqlAsync("""
            CREATE TABLE TorrentUpdateCounter (Value INTEGER NOT NULL);
            INSERT INTO TorrentUpdateCounter VALUES (0);
            CREATE TRIGGER CountTorrentUpdate AFTER UPDATE ON Torrents
            BEGIN UPDATE TorrentUpdateCounter SET Value = Value + 1; END;
            CREATE TRIGGER RejectSecondTorrentUpdate BEFORE UPDATE ON Torrents
            WHEN (SELECT Value FROM TorrentUpdateCounter) = 1
            BEGIN SELECT RAISE(ABORT, 'second torrent update rejected'); END;
            """);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();

        await AssertTotalsAsync(fixture, (previouslyCommitted.Id, 110, 220), (other.Id, 300, 400));
        previouslyCommitted.BaselineFor(110, 220).Should().Be((10, 20));

        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER RejectSecondTorrentUpdate;");
        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER CountTorrentUpdate;");
        await fixture.Database.ExecuteSqlAsync("DROP TABLE TorrentUpdateCounter;");
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (previouslyCommitted.Id, 115, 227), (other.Id, 305, 407));
    }

    [Fact]
    public async Task FlushAsync_WhenLaterTrafficReadFails_PreservesPreviousCheckpoint()
    {
        var afterSave = new ThrowAfterSaveInterceptor();
        await using var fixture = await Fixture.CreateAsync(afterSave);
        var addedFirst = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        var addedSecond = await fixture.AddDistinctTorrentAsync(downloaded: 100, uploaded: 200);

        await fixture.Service.FlushAsync(default);
        var first = fixture.Service.Reads[0] == addedFirst.Id ? addedFirst : addedSecond;
        var second = first.Id == addedFirst.Id ? addedSecond : addedFirst;
        fixture.Service.Reads.Clear();

        fixture.Service.SetTraffic(first.Id, 10, 20);
        afterSave.Failure = new IOException("confirmation lost");
        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<IOException>();

        fixture.Service.Reads.Should().Equal(first.Id, second.Id);
        await AssertTotalsAsync(fixture, (first.Id, 110, 220), (second.Id, 100, 200));
        first.PendingFlush.Should().Be(new TorrentEngineService.ManagedTorrent.TrafficCheckpoint(110, 220, 10, 20));
        first.BaselineFor(110, 220).Should().Be((10, 20));

        fixture.Service.Reads.Clear();
        fixture.Service.SetTraffic(first.Id, 15, 27);
        fixture.Service.SetTraffic(second.Id, 5, 7);
        fixture.Service.ThrowOnReadNumber = 2;
        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<IOException>();

        fixture.Service.Reads.Should().Equal(first.Id, second.Id);
        await AssertTotalsAsync(fixture, (first.Id, 110, 220), (second.Id, 100, 200));
        first.PendingFlush.Should().Be(new TorrentEngineService.ManagedTorrent.TrafficCheckpoint(110, 220, 10, 20));
        first.BaselineFor(110, 220).Should().Be((10, 20));

        fixture.Service.ThrowOnReadNumber = null;
        fixture.Service.SetTraffic(first.Id, 20, 30);
        fixture.Service.SetTraffic(second.Id, 8, 12);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (first.Id, 120, 230), (second.Id, 108, 212));
        first.LastSessionDownloaded.Should().Be(20);
        first.LastSessionUploaded.Should().Be(30);
        first.PendingFlush.Should().BeNull();
    }

    [Fact]
    public async Task FlushAsync_WhenTrafficDeltaIsZeroAndSaveFails_DoesNotMoveBaselineOrTotals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        await fixture.Database.ExecuteSqlAsync("UPDATE Torrents SET Status = -999;");
        var before = await fixture.Database.ReadAsync(managed.Id);
        before.Should().NotBeNull();
        before!.Status.Should().Be(-999);
        fixture.Service.SetTraffic(managed.Id, 0, 0);
        await fixture.Database.ExecuteSqlAsync("""
            CREATE TRIGGER RejectTorrentUpdate BEFORE UPDATE ON Torrents
            BEGIN SELECT RAISE(ABORT, 'torrent update rejected'); END;
            """);

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateException>();

        var after = await fixture.Database.ReadAsync(managed.Id);
        after.Should().NotBeNull();
        after.Should().BeEquivalentTo(before);
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);
        managed.PendingFlush.Should().BeNull();

        await fixture.Database.ExecuteSqlAsync("DROP TRIGGER RejectTorrentUpdate;");
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        await fixture.Service.FlushAsync(default);

        await AssertTotalsAsync(fixture, (managed.Id, 110, 220));
        managed.LastSessionDownloaded.Should().Be(10);
        managed.LastSessionUploaded.Should().Be(20);
        managed.PendingFlush.Should().BeNull();
    }

    [Fact]
    public async Task FlushAsync_WhenRowIsDeletedAfterRead_LeavesBaselineAndSkipsItNextTick()
    {
        var beforeSave = new SaveChangesCallbackInterceptor();
        await using var fixture = await Fixture.CreateAsync(beforeSave);
        var managed = await fixture.AddTorrentAsync(downloaded: 100, uploaded: 200);
        fixture.Service.SetTraffic(managed.Id, 10, 20);
        beforeSave.OnSaving = async () =>
        {
            await using var context = fixture.Database.CreateAdditionalContext();
            var entity = await context.Torrents.SingleAsync(x => x.Id == managed.Id);
            context.Torrents.Remove(entity);
            await context.SaveChangesAsync();
        };

        await FluentActions.Awaiting(() => fixture.Service.FlushAsync(default))
            .Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await fixture.Database.ReadAsync(managed.Id)).Should().BeNull();
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);
        beforeSave.OnSaving = null;
        await fixture.Service.FlushAsync(default);
        managed.LastSessionDownloaded.Should().Be(0);
        managed.LastSessionUploaded.Should().Be(0);
    }

    private static async Task AssertTotalsAsync(Fixture fixture, params (Guid Id, long Downloaded, long Uploaded)[] expected)
    {
        foreach (var (id, downloaded, uploaded) in expected)
        {
            var entity = await fixture.Database.ReadAsync(id);
            entity.Should().NotBeNull();
            entity!.Downloaded.Should().Be(downloaded);
            entity.Uploaded.Should().Be(uploaded);
        }
    }

    private static void SetMonitorCounter(object monitor, string propertyName, long value)
    {
        // Частные детали MonoTorrent 3.0.2; это нужно только для проверки, что TorrentMapper.ToInfo читает Monitor напрямую.
        var type = monitor.GetType();
        var property = type.GetProperty(propertyName)!;

        var backingField = propertyName == nameof(ConnectionMonitor.DataBytesReceived) ? "DataDown" : "DataUp";
        var speedMonitorField = type.GetField($"<{backingField}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var speedMonitor = speedMonitorField.GetValue(monitor)!;
        speedMonitor.GetType().GetMethod("AddDelta")!.Invoke(speedMonitor, [checked((int)value)]);
        property.GetValue(monitor).Should().Be(value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(EngineHarness harness, SqliteTorrentDatabase database, TestPersistenceService service)
        {
            Harness = harness;
            Database = database;
            Service = service;
        }

        public EngineHarness Harness { get; }
        public SqliteTorrentDatabase Database { get; }
        public TestPersistenceService Service { get; }

        public static async Task<Fixture> CreateAsync(params IInterceptor[] interceptors)
        {
            var harness = new EngineHarness();
            await harness.InitializeAsync();
            try
            {
                var database = new SqliteTorrentDatabase(interceptors);
                return new Fixture(harness, database, new TestPersistenceService(harness.Engine, database.ScopeFactory));
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        public async Task<TorrentEngineService.ManagedTorrent> AddTorrentAsync(long downloaded = 0, long uploaded = 0)
        {
            var managed = await Harness.AddAsync();
            await Database.SeedAsync(CreateEntity(managed, downloaded, uploaded));
            return managed;
        }

        public async Task<TorrentEngineService.ManagedTorrent> AddDistinctTorrentAsync(long downloaded = 0, long uploaded = 0)
        {
            var managed = await Harness.AddDistinctAsync();
            await Database.SeedAsync(CreateEntity(managed, downloaded, uploaded));
            return managed;
        }

        public Task<TorrentEngineService.ManagedTorrent> AddDistinctManagedTorrentAsync()
            => Harness.AddDistinctAsync();

        public static TorrentEntity CreateEntity(TorrentEngineService.ManagedTorrent managed, long downloaded, long uploaded)
        {
            var manager = managed.Manager;
            return new TorrentEntity
            {
                Id = managed.Id,
                UserId = 7,
                InfoHash = managed.Id.ToString("N"),
                Name = "seeded torrent",
                SavePath = "/tmp/torrents",
                Status = (int)TorrentMapper.MapStatus(manager.State, manager.Complete, paused: false),
                TotalSize = manager.HasMetadata && manager.Torrent != null ? manager.Torrent.Size : 0,
                Downloaded = downloaded,
                Uploaded = uploaded,
                Progress = manager.Progress / 100.0,
                CompletedAt = manager.Complete ? DateTime.UtcNow : null,
                AddedAt = DateTime.UtcNow,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Harness.DisposeAsync();
            Database.Dispose();
        }
    }

    private sealed class TestPersistenceService(TorrentEngineService engine, IServiceScopeFactory scopeFactory)
        : TorrentPersistenceService(engine, scopeFactory, TestLoggers.Null<TorrentPersistenceService>())
    {
        private readonly Dictionary<Guid, (long Received, long Sent)> _traffic = new();

        public List<Guid> Reads { get; } = [];
        public int? ThrowOnReadNumber { get; set; }

        public void SetTraffic(Guid id, long received, long sent) => _traffic[id] = (received, sent);

        protected override (long Received, long Sent) ReadTraffic(TorrentEngineService.ManagedTorrent managed)
        {
            Reads.Add(managed.Id);
            if (ThrowOnReadNumber == Reads.Count)
                throw new IOException("traffic read failed");
            return _traffic.GetValueOrDefault(managed.Id);
        }
    }

    private sealed class SaveChangesCallbackInterceptor : SaveChangesInterceptor
    {
        public Action? OnSaved { get; set; }
        public Func<Task>? OnSaving { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (OnSaving != null)
                await OnSaving();
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            OnSaved?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelBeforeSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? Failure { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var failure = Failure;
            Failure = null;
            if (failure != null)
                throw failure;
            return ValueTask.FromResult(result);
        }
    }
}
