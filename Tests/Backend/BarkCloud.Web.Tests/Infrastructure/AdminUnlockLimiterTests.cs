using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public class AdminUnlockLimiterTests
{
    private const string Ip = "203.0.113.7";

    private readonly ManualTimeProvider _time = new();

    private AdminUnlockLimiter CreateSut() => new(_time);

    [Fact]
    public void TryAcquire_AllowsFiveAttemptsPerIpThenBlocks()
    {
        var sut = CreateSut();

        for (var i = 0; i < 5; i++)
        {
            sut.TryAcquire(Ip).Allowed.Should().BeTrue($"попытка {i + 1} в пределах лимита");
        }

        var blocked = sut.TryAcquire(Ip);

        blocked.Allowed.Should().BeFalse();
        blocked.RetryAfter.Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryAcquire_BlockedAttemptsDoNotExtendTheWindow()
    {
        var sut = CreateSut();
        for (var i = 0; i < 5; i++) sut.TryAcquire(Ip);

        _time.Advance(TimeSpan.FromMinutes(10));
        sut.TryAcquire(Ip).Allowed.Should().BeFalse();
        _time.Advance(TimeSpan.FromMinutes(5));

        sut.TryAcquire(Ip).Allowed.Should().BeTrue("окно открылось первой попыткой и закончилось через 15 минут");
    }

    [Fact]
    public void TryAcquire_DifferentIpsHaveOwnBuckets()
    {
        var sut = CreateSut();
        for (var i = 0; i < 5; i++) sut.TryAcquire(Ip);

        sut.TryAcquire(Ip).Allowed.Should().BeFalse();
        sut.TryAcquire("198.51.100.9").Allowed.Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_GlobalBudgetStopsRotatingIps()
    {
        var sut = CreateSut();

        for (var i = 0; i < 20; i++)
        {
            sut.TryAcquire($"198.51.100.{i}").Allowed.Should().BeTrue($"попытка {i + 1} с нового адреса в пределах общего бюджета");
        }

        // 21-я с ещё ни разу не использованного адреса — отказ: общий бюджет часа исчерпан.
        var blocked = sut.TryAcquire("192.0.2.77");

        blocked.Allowed.Should().BeFalse();
        blocked.RetryAfter.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryAcquire_GlobalBudgetRenewsAfterAnHour()
    {
        var sut = CreateSut();
        for (var i = 0; i < 20; i++) sut.TryAcquire($"198.51.100.{i}");

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        sut.TryAcquire(Ip).Allowed.Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_UnknownIp_SharesOneBucket()
    {
        var sut = CreateSut();
        for (var i = 0; i < 5; i++) sut.TryAcquire(null);

        sut.TryAcquire("").Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_ParallelAttempts_NeverExceedPerIpLimit()
    {
        var sut = CreateSut();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => sut.TryAcquire(Ip).Allowed)));

        results.Count(allowed => allowed).Should().Be(5);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
