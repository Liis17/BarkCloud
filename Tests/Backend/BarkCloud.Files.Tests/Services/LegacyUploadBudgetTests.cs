using BarkCloud.Files.Services;

namespace BarkCloud.Files.Tests.Services;

public class LegacyUploadBudgetTests
{
    private static LegacyUploadOptions Options(int slots = 2, long bytes = 100, int queueMs = 50) => new()
    {
        MaxConcurrent = slots,
        MaxBufferedBytes = bytes,
        QueueTimeout = TimeSpan.FromMilliseconds(queueMs)
    };

    [Fact]
    public async Task TryAcquireAsync_CountsBytesAndReleasesThemOnDispose()
    {
        var sut = new LegacyUploadBudget(Options());

        var lease = await sut.TryAcquireAsync(40, default);

        lease.Should().NotBeNull();
        sut.BufferedBytes.Should().Be(40);
        lease!.Dispose();
        sut.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenByteBudgetIsExceeded_RejectsAndKeepsSlotAndBytes()
    {
        var sut = new LegacyUploadBudget(Options(slots: 1, bytes: 100));
        using var first = await sut.TryAcquireAsync(70, default);

        var second = await sut.TryAcquireAsync(40, default);

        second.Should().BeNull();
        sut.BufferedBytes.Should().Be(70);
        first!.Dispose();
        // Слот отклонённого запроса вернулся: следующий запрос проходит.
        using var third = await sut.TryAcquireAsync(40, default);
        third.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenNoSlotFreesUpInTime_ReturnsNull()
    {
        var sut = new LegacyUploadBudget(Options(slots: 1));
        using var first = await sut.TryAcquireAsync(1, default);

        var second = await sut.TryAcquireAsync(1, default);

        second.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenSlotFreesUpWhileWaiting_Succeeds()
    {
        var sut = new LegacyUploadBudget(Options(slots: 1, queueMs: 5000));
        var first = await sut.TryAcquireAsync(1, default);

        var waiting = sut.TryAcquireAsync(1, default);
        first!.Dispose();

        (await waiting).Should().NotBeNull();
    }

    [Fact]
    public async Task Dispose_CalledTwice_ReleasesOnlyOnce()
    {
        var sut = new LegacyUploadBudget(Options(slots: 1));
        var lease = await sut.TryAcquireAsync(30, default);

        lease!.Dispose();
        lease.Dispose();

        sut.BufferedBytes.Should().Be(0);
        using var next = await sut.TryAcquireAsync(1, default);
        next.Should().NotBeNull();
        // Второй Dispose не вернул лишний слот: параллельно больше одного не проходит.
        (await sut.TryAcquireAsync(1, default)).Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenBodyExceedsBudgetOnlyByOverhead_StillAdmitsAlone()
    {
        var sut = new LegacyUploadBudget(Options(bytes: 100));

        using var lease = await sut.TryAcquireAsync(150, default);

        lease.Should().NotBeNull();
        sut.BufferedBytes.Should().Be(100);
    }
}
