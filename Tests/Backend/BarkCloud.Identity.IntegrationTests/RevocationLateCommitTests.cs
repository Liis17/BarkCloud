using System.Threading.Channels;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Identity.IntegrationTests;

public class RevocationLateCommitTests
{
    [Fact]
    public async Task LongTransaction_CommitAfterOverlap_IsDeliveredByFullResyncAndRejectsOldJwt()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var services = new ServiceCollection().AddScoped(_ => database.CreateContext()).BuildServiceProvider();
        var feed = new PollRecordingFeed(new DbRevocationFeed(services.GetRequiredService<IServiceScopeFactory>()));
        var clock = new TestClock();
        var cache = new TokenRevocationCache();
        using var sut = new RevocationSyncService(cache, feed, NullLogger<RevocationSyncService>.Instance, clock);

        // Транзакция отзыва ещё не закоммичена; метка времени старше минутного overlap
        // (моделирует задержку commit без физического ожидания).
        var revokedAt = DateTime.UtcNow.AddMinutes(-2);
        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        writer.RevokedSessions.Add(new RevokedSession
        {
            UserId = 42,
            DeviceId = "phone",
            RevokedAt = revokedAt,
            ExpiresAt = revokedAt.AddMinutes(61)
        });
        await writer.SaveChangesAsync();
        var oldJwtIssuedAt = revokedAt.AddSeconds(-1);

        await sut.StartAsync(default);
        await clock.NextDelay();
        await Tick(clock, 3);
        cache.IsRevoked(42, "phone", oldJwtIssuedAt).Should().BeFalse("транзакция ещё не закоммичена");

        await transaction.CommitAsync();
        await Tick(clock, 8);
        cache.IsRevoked(42, "phone", oldJwtIssuedAt).Should().BeFalse("incremental-опрос пропускает запись старше overlap");
        feed.Requests.Count(x => x is null).Should().Be(1, "до минуты есть только стартовый snapshot");

        await Tick(clock, 1);

        cache.IsRevoked(42, "phone", oldJwtIssuedAt).Should().BeTrue("полная сверка находит закоммиченный отзыв");
        feed.Requests.Count(x => x is null).Should().Be(2);
        await sut.StopAsync(default);
    }

    private static async Task Tick(TestClock clock, int count)
    {
        for (var i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.NextDelay();
        }
    }

    private sealed class PollRecordingFeed(IRevocationFeed inner) : IRevocationFeed
    {
        public List<DateTime?> Requests { get; } = [];

        public Task<RevocationBatch> FetchAsync(DateTime? changedSince, CancellationToken cancellationToken)
        {
            Requests.Add(changedSince);
            return inner.FetchAsync(changedSince, cancellationToken);
        }
    }

    private sealed class TestClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextDelay()
            => await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
