using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;
using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayStorageTests
{
    [Fact]
    public async Task GetStorage_coalesces_concurrent_requests_and_uses_15_second_deadline()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRequest = new ManualResetEventSlim();
        var calls = 0;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    requestStarted.TrySetResult();
                    releaseRequest.Wait();
                }
                return new GetUserStorageInfoResponse { StorageLimit = call };
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var before = DateTime.UtcNow;
        var first = Task.Run(gateway.GetStorage);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(11));
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(() =>
        {
            secondStarted.TrySetResult();
            return gateway.GetStorage();
        });
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        var secondWasWaitingForFirst = !second.IsCompleted;

        releaseRequest.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        secondWasWaitingForFirst.Should().BeTrue();
        results.Select(result => result.Info.StorageLimit).Should().Equal(1, 1);
        calls.Should().Be(1);
        var deadline = cloud.Invoker.Calls.Single().Deadline;
        deadline.Should().NotBeNull();
        var after = DateTime.UtcNow;
        deadline!.Value.Should().BeOnOrAfter(before.AddSeconds(15))
            .And.BeOnOrBefore(after.AddSeconds(15));
    }

    [Fact]
    public void GetStorage_returns_stale_value_and_negative_caches_rpc_failure()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var calls = 0;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return new GetUserStorageInfoResponse { StorageLimit = 500 };
                throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        gateway.GetStorage().Info.StorageLimit.Should().Be(500);
        time.Advance(TimeSpan.FromSeconds(11));

        gateway.GetStorage().Info.StorageLimit.Should().Be(500);
        gateway.GetStorage().Info.StorageLimit.Should().Be(500);

        calls.Should().Be(2);
    }

    [Fact]
    public void GetStorage_rethrows_failure_without_cache_and_negative_caches_it()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.Invoker.FailNext(FakeCloudMethods.GetUserStorageInfo,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());

        Action getStorage = () => gateway.GetStorage();
        getStorage.Should().Throw<RpcException>();
        getStorage.Should().Throw<RpcException>();

        cloud.Invoker.Calls.Should().ContainSingle(call => call.MethodFullName == FakeCloudMethods.GetUserStorageInfo);
    }

    [Fact]
    public async Task GetStorage_does_not_publish_a_late_result_after_account_cache_clear()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    releaseFirst.Task.GetAwaiter().GetResult();
                    return new GetUserStorageInfoResponse { StorageLimit = 100 };
                }

                return new GetUserStorageInfoResponse { StorageLimit = 200 };
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());

        var oldRequest = StartLongRunning(gateway.GetStorage);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ClearCache();
        gateway.GetStorage().Info.StorageLimit.Should().Be(200);
        releaseFirst.TrySetResult();

        var act = async () => await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        gateway.GetStorage().Info.StorageLimit.Should().Be(200);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetStorage_does_not_return_old_quota_when_old_account_request_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var failureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                    return new GetUserStorageInfoResponse { StorageLimit = 100 };
                if (call == 2)
                {
                    failureStarted.TrySetResult();
                    releaseFailure.Task.GetAwaiter().GetResult();
                    throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
                }

                return new GetUserStorageInfoResponse { StorageLimit = 200 };
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        gateway.GetStorage().Info.StorageLimit.Should().Be(100);
        time.Advance(TimeSpan.FromSeconds(11));
        var oldRequest = StartLongRunning(gateway.GetStorage);
        await failureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ClearCache();
        releaseFailure.TrySetResult();

        var act = async () => await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<RpcException>();
        gateway.GetStorage().Info.StorageLimit.Should().Be(200);
        calls.Should().Be(3);
    }

    [Fact]
    public void GetStorage_requests_non_blocking_server_snapshots()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        GetUserStorageInfoRequest? seen = null;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, request =>
            {
                seen = request;
                return new GetUserStorageInfoResponse { StorageLimit = 500 };
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());

        gateway.GetStorage().Info.StorageLimit.Should().Be(500);

        seen.Should().NotBeNull();
        seen!.NonBlockingStats.Should().BeTrue();
    }

    [Fact]
    public void GetStorage_keeps_stale_data_with_refresh_error_and_recovers()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var calls = 0;
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 2)
                    throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
                return new GetUserStorageInfoResponse { StorageLimit = 100 + call };
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        var fresh = gateway.GetStorage();
        fresh.Info.StorageLimit.Should().Be(101);
        fresh.RefreshError.Should().BeNull();

        time.Advance(TimeSpan.FromSeconds(11));
        var stale = gateway.GetStorage();
        stale.Info.StorageLimit.Should().Be(101);
        stale.RefreshError.Should().NotBeNull();
        stale.RefreshError!.SourceException.Should().BeOfType<RpcException>();

        // В окне негативного кеша повторный вызов отдаёт те же данные и ту же ошибку.
        gateway.GetStorage().Info.StorageLimit.Should().Be(101);
        calls.Should().Be(2);

        time.Advance(TimeSpan.FromSeconds(11));
        var recovered = gateway.GetStorage();
        recovered.Info.StorageLimit.Should().Be(103);
        recovered.RefreshError.Should().BeNull();
        calls.Should().Be(3);
    }

    private static Task<T> StartLongRunning<T>(Func<T> action)
        => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
