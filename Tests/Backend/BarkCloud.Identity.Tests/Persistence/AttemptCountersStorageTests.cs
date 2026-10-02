using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public class AttemptCountersStorageTests : IDisposable
{
    private const string Key = "login:7";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly SqliteIdentityContext _db = new();
    private readonly AttemptCountersStorage _sut;

    public AttemptCountersStorageTests()
    {
        _sut = new AttemptCountersStorage(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task MoveWindowEnd(string key, DateTime windowEndsAt)
    {
        using var ctx = _db.CreateAdditionalContext();
        var row = await ctx.AuthAttemptCounters.SingleAsync(x => x.Key == key);
        row.WindowEndsAt = windowEndsAt;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> RowCount()
    {
        using var ctx = _db.CreateAdditionalContext();
        return await ctx.AuthAttemptCounters.CountAsync();
    }

    [Fact]
    public async Task TryReserve_NoRow_CreatesRowWithFirstAttempt()
    {
        var result = await _sut.TryReserve(Key, 3, Window);

        result.Allowed.Should().BeTrue();
        using var ctx = _db.CreateAdditionalContext();
        var row = await ctx.AuthAttemptCounters.AsNoTracking().SingleAsync();
        row.Count.Should().Be(1);
        row.WindowEndsAt.Should().BeCloseTo(DateTime.UtcNow.Add(Window), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TryReserve_AllowsMaxAttemptsThenBlocksWithRetryAfter()
    {
        for (var i = 0; i < 3; i++)
        {
            (await _sut.TryReserve(Key, 3, Window)).Allowed.Should().BeTrue($"попытка {i + 1} в пределах лимита");
        }

        var blocked = await _sut.TryReserve(Key, 3, Window);

        blocked.Allowed.Should().BeFalse();
        blocked.RetryAfter.Should().BeGreaterThan(TimeSpan.FromMinutes(14)).And.BeLessThanOrEqualTo(Window);
        (await _sut.TryReserve(Key, 3, Window)).Allowed.Should().BeFalse();

        using var ctx = _db.CreateAdditionalContext();
        (await ctx.AuthAttemptCounters.AsNoTracking().SingleAsync()).Count.Should().Be(3, "отказы не увеличивают счётчик");
    }

    [Fact]
    public async Task TryReserve_AfterWindowEnds_StartsNewWindow()
    {
        await _sut.TryReserve(Key, 1, Window);
        (await _sut.TryReserve(Key, 1, Window)).Allowed.Should().BeFalse();

        await MoveWindowEnd(Key, DateTime.UtcNow.AddSeconds(-1));

        (await _sut.TryReserve(Key, 1, Window)).Allowed.Should().BeTrue();
        (await _sut.TryReserve(Key, 1, Window)).Allowed.Should().BeFalse("новое окно тоже ограничено");
    }

    [Fact]
    public async Task TryReserve_DifferentKeysAreIndependent()
    {
        await _sut.TryReserve("login:1", 1, Window);

        (await _sut.TryReserve("login:1", 1, Window)).Allowed.Should().BeFalse();
        (await _sut.TryReserve("login:2", 1, Window)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_RemovesCounter_SoLimitStartsOver()
    {
        await _sut.TryReserve(Key, 1, Window);
        (await _sut.TryReserve(Key, 1, Window)).Allowed.Should().BeFalse();

        await _sut.Reset(Key);

        (await _sut.TryReserve(Key, 1, Window)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_UnknownKey_DoesNothing()
    {
        await _sut.Reset("missing");

        (await RowCount()).Should().Be(0);
    }

    [Fact]
    public async Task TryReserve_NewRow_RemovesLongExpiredRows()
    {
        await _sut.TryReserve("old", 1, Window);
        await MoveWindowEnd("old", DateTime.UtcNow.AddHours(-2));
        await _sut.TryReserve("recent", 1, Window);
        await MoveWindowEnd("recent", DateTime.UtcNow.AddMinutes(-5));

        await _sut.TryReserve("fresh", 1, Window);

        using var ctx = _db.CreateAdditionalContext();
        var keys = await ctx.AuthAttemptCounters.Select(x => x.Key).ToListAsync();
        keys.Should().BeEquivalentTo("recent", "fresh");
    }

    [Fact]
    public async Task TryReserve_ParallelRequests_AllowExactlyMax()
    {
        const int max = 5;

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            using var ctx = _db.CreateAdditionalContext();
            return await new AttemptCountersStorage(ctx).TryReserve(Key, max, Window);
        }));

        results.Count(r => r.Allowed).Should().Be(max);
    }
}
