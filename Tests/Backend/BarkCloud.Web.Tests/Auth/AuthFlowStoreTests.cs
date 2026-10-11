namespace BarkCloud.Web.Tests.Auth;

public sealed class AuthFlowStoreTests
{
    [Fact]
    public async Task IdleFlowExpiresAutomaticallyAndClearsItsSecrets()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(
            lifetime: TimeSpan.FromMilliseconds(120), cleanupInterval: TimeSpan.FromMilliseconds(10));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, "device", out var flow).Should().BeTrue();
        var createdFlow = flow!;
        SetPassword(createdFlow, "secret-password");

        await WaitUntilAsync(() => createdFlow.Password is null, TimeSpan.FromSeconds(2));

        createdFlow.Password.Should().BeNull();
        store.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAfterWaitExpiresEveryDueFlowAndClearsSecrets()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(
            lifetime: TimeSpan.FromMilliseconds(140), cleanupInterval: TimeSpan.FromSeconds(30));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, "device", out var waitingFlow).Should().BeTrue();
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.PasswordReset, "device", out var idleFlow).Should().BeTrue();
        var activeFlow = waitingFlow!;
        var otherFlow = idleFlow!;
        SetPassword(activeFlow, "waiting-secret");
        SetPassword(otherFlow, "idle-secret");
        var firstLease = await store.AcquireAsync(activeFlow.FlowId, "device",
            BarkCloud.Web.Auth.AuthFlowKind.Login);
        firstLease.Should().NotBeNull();

        var queuedAcquire = store.AcquireAsync(activeFlow.FlowId, "device",
            BarkCloud.Web.Auth.AuthFlowKind.Login);
        await Task.Delay(220);
        firstLease!.Dispose();

        (await queuedAcquire).Should().BeNull();
        activeFlow.Password.Should().BeNull();
        otherFlow.Password.Should().BeNull();
        store.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task ExpiryCancelsQueuedAcquireAsMissingFlowAndDefersSecretClearUntilLeaseRelease()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(
            lifetime: TimeSpan.FromMilliseconds(120), cleanupInterval: TimeSpan.FromMilliseconds(10));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, "device", out var flow).Should().BeTrue();
        var activeFlow = flow!;
        SetPassword(activeFlow, "secret-password");
        var firstLease = await store.AcquireAsync(activeFlow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Login);
        var queuedAcquire = store.AcquireAsync(activeFlow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Login);

        (await queuedAcquire.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
        activeFlow.Password.Should().Be("secret-password");
        firstLease!.Dispose();

        activeFlow.Password.Should().BeNull();
        store.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireRechecksExpectedKindAfterWaitingForFlowLock()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(
            lifetime: TimeSpan.FromSeconds(5), cleanupInterval: TimeSpan.FromSeconds(30));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Registration, "device", out var flow).Should().BeTrue();
        var firstLease = await store.AcquireAsync(flow!.FlowId, "device",
            BarkCloud.Web.Auth.AuthFlowKind.Registration);
        var queuedAcquire = store.AcquireAsync(flow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Registration);

        typeof(BarkCloud.Web.Auth.AuthFlow).GetProperty(nameof(BarkCloud.Web.Auth.AuthFlow.Kind))!
            .SetValue(flow, BarkCloud.Web.Auth.AuthFlowKind.SessionPending);
        firstLease!.Dispose();

        (await queuedAcquire).Should().BeNull();
        using var pendingLease = await store.AcquireAsync(flow.FlowId, "device",
            BarkCloud.Web.Auth.AuthFlowKind.SessionPending);
        pendingLease.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelledAcquireStopsWaitingWithoutMutatingFlow()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(
            lifetime: TimeSpan.FromSeconds(5), cleanupInterval: TimeSpan.FromSeconds(30));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, "device", out var flow).Should().BeTrue();
        var firstLease = await store.AcquireAsync(flow!.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Login);
        using var cancellation = new CancellationTokenSource();
        var queuedAcquire = store.AcquireAsync(flow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Login,
            cancellation.Token);
        await Task.Delay(20);
        cancellation.Cancel();

        await FluentActions.Awaiting(() => queuedAcquire).Should().ThrowAsync<OperationCanceledException>();
        store.IsActive(flow).Should().BeTrue();
        firstLease!.Dispose();
        using var nextLease = await store.AcquireAsync(flow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.Login);
        nextLease.Should().NotBeNull();
    }

    [Fact]
    public void CapacityRejectsNewFlowsWithoutEvictingExistingFlows()
    {
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(cleanupInterval: TimeSpan.FromHours(1));
        BarkCloud.Web.Auth.AuthFlow? first = null;
        for (var index = 0; index < BarkCloud.Web.Auth.AuthFlowStore.Capacity; index++)
        {
            store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, $"device-{index}", out var flow)
                .Should().BeTrue();
            if (index == 0) first = flow;
        }

        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.Login, "overflow", out var rejected).Should().BeFalse();
        rejected.Should().BeNull();
        store.ActiveCount.Should().Be(BarkCloud.Web.Auth.AuthFlowStore.Capacity);
        store.IsActive(first!).Should().BeTrue();
    }

    [Fact]
    public async Task AcquiringFlowDoesNotExtendItsAbsoluteLifetime()
    {
        var clock = new ManualTimeProvider();
        using var store = new BarkCloud.Web.Auth.AuthFlowStore(clock, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1));
        store.TryCreate(BarkCloud.Web.Auth.AuthFlowKind.PasswordReset, "device", out var flow).Should().BeTrue();
        var expiresAt = flow!.ExpiresAt;

        clock.Advance(TimeSpan.FromMinutes(14));
        using (var lease = await store.AcquireAsync(flow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.PasswordReset))
            lease.Should().NotBeNull();
        flow.ExpiresAt.Should().Be(expiresAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        (await store.AcquireAsync(flow.FlowId, "device", BarkCloud.Web.Auth.AuthFlowKind.PasswordReset)).Should().BeNull();
        flow.ExpiresAt.Should().Be(expiresAt);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= stopAt)
                throw new TimeoutException("The expected flow state was not reached before the test timeout.");
            await Task.Delay(10);
        }
    }

    private static void SetPassword(BarkCloud.Web.Auth.AuthFlow flow, string value)
        => typeof(BarkCloud.Web.Auth.AuthFlow).GetProperty(nameof(BarkCloud.Web.Auth.AuthFlow.Password))!
            .SetValue(flow, value);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan by) => _now += by;
    }
}
