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
        results.Select(result => result.StorageLimit).Should().Equal(1, 1);
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

        gateway.GetStorage().StorageLimit.Should().Be(500);
        time.Advance(TimeSpan.FromSeconds(11));

        gateway.GetStorage().StorageLimit.Should().Be(500);
        gateway.GetStorage().StorageLimit.Should().Be(500);

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
}
