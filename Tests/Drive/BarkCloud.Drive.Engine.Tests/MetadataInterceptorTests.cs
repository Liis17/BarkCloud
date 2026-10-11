using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;

using Grpc.Core.Interceptors;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class MetadataInterceptorTests
{
    [Fact]
    public async Task Async_unary_call_without_deadline_gets_default_deadline()
    {
        var invoker = CreateInvoker();
        var client = new FilesApi.FilesApiClient(invoker.Intercept(CreateInterceptor()));
        var before = DateTime.UtcNow.AddSeconds(27);

        using var call = client.GetUserStorageInfoAsync(new GetUserStorageInfoRequest());
        await call.ResponseAsync;

        invoker.Calls.Single().Deadline.Should().BeAfter(before).And.BeBefore(DateTime.UtcNow.AddSeconds(33));
    }

    [Fact]
    public void Unary_call_without_deadline_gets_default_deadline()
    {
        var invoker = CreateInvoker();
        var client = new FilesApi.FilesApiClient(invoker.Intercept(CreateInterceptor()));
        var before = DateTime.UtcNow.AddSeconds(27);

        client.GetUserStorageInfo(new GetUserStorageInfoRequest());

        invoker.Calls.Single().Deadline.Should().BeAfter(before).And.BeBefore(DateTime.UtcNow.AddSeconds(33));
    }

    [Fact]
    public void Unary_call_keeps_explicit_deadline()
    {
        var invoker = CreateInvoker();
        var client = new FilesApi.FilesApiClient(invoker.Intercept(CreateInterceptor()));
        var deadline = DateTime.UtcNow.AddMinutes(2);

        client.GetUserStorageInfo(new GetUserStorageInfoRequest(), deadline: deadline);

        invoker.Calls.Single().Deadline.Should().Be(deadline);
    }

    private static FakeCallInvoker CreateInvoker()
    {
        var invoker = new FakeCallInvoker();
        invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ => new GetUserStorageInfoResponse());
        return invoker;
    }

    private static MetadataInterceptor CreateInterceptor()
    {
        var device = new DeviceIdentity("test-id", "test-device", "test-os", "test-app", "1.0");
        return new MetadataInterceptor(device, () => null);
    }
}
