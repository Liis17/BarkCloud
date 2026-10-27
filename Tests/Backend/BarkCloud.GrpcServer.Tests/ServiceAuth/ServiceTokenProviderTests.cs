using BarkCloud.GrpcServer.ServiceAuth;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

namespace BarkCloud.GrpcServer.Tests.ServiceAuth;

public class ServiceTokenProviderTests
{
    private static readonly string Secret = "test-service-credential";

    private static GetServiceTokenResponse Response(TimeSpan lifetime) => new()
    {
        Token = $"svc-token-{Guid.NewGuid():N}",
        ExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.Add(lifetime))
    };

    private static AsyncUnaryCall<TResponse> Result<TResponse>(TResponse response) => new(
        Task.FromResult(response),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });

    private static AsyncUnaryCall<TResponse> Failure<TResponse>() => new(
        Task.FromException<TResponse>(new RpcException(new Status(StatusCode.Unavailable, "identity down"))),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });

    private static (Mock<CallInvoker> Invoker, ServiceTokenProvider Provider, FakeTimeProvider Time) Build()
    {
        var invoker = new Mock<CallInvoker>();
        var client = new ServiceCredentialsApi.ServiceCredentialsApiClient(invoker.Object);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new ServiceTokenProvider(client, Secret, time, NullLogger<ServiceTokenProvider>.Instance);
        return (invoker, provider, time);
    }

    private static void SetupExchange(Mock<CallInvoker> invoker, GetServiceTokenResponse response) =>
        invoker
            .Setup(i => i.AsyncUnaryCall(
                It.IsAny<Method<GetServiceTokenRequest, GetServiceTokenResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>(),
                It.Is<GetServiceTokenRequest>(r => r.Credential == Secret)))
            .Returns(Result(response));

    [Fact]
    public async Task GetTokenAsync_ExchangesCredentialForTarget()
    {
        var (invoker, provider, _) = Build();
        var response = Response(TimeSpan.FromMinutes(10));
        SetupExchange(invoker, response);

        var token = await provider.GetTokenAsync(ServiceId.Users);

        token.Should().Be(response.Token);
        invoker.Verify(i => i.AsyncUnaryCall(
            It.IsAny<Method<GetServiceTokenRequest, GetServiceTokenResponse>>(),
            It.IsAny<string>(),
            It.IsAny<CallOptions>(),
            It.Is<GetServiceTokenRequest>(r =>
                r.Credential == Secret && r.TargetService == (int)ServiceId.Users)), Times.Once);
    }

    [Fact]
    public async Task GetTokenAsync_CachesPerTargetUntilRefreshMargin()
    {
        var (invoker, provider, time) = Build();
        var response = Response(TimeSpan.FromMinutes(10));
        SetupExchange(invoker, response);

        await provider.GetTokenAsync(ServiceId.Users);
        time.Advance(TimeSpan.FromMinutes(8));
        await provider.GetTokenAsync(ServiceId.Users);

        invoker.Verify(i => i.AsyncUnaryCall(
            It.IsAny<Method<GetServiceTokenRequest, GetServiceTokenResponse>>(),
            It.IsAny<string>(),
            It.IsAny<CallOptions>(),
            It.IsAny<GetServiceTokenRequest>()), Times.Once);
    }

    [Fact]
    public async Task GetTokenAsync_RefreshesAfterMargin()
    {
        var (invoker, provider, time) = Build();
        var first = Response(TimeSpan.FromMinutes(10));
        SetupExchange(invoker, first);

        await provider.GetTokenAsync(ServiceId.Users);
        time.Advance(TimeSpan.FromMinutes(10));
        var second = Response(TimeSpan.FromMinutes(10));
        SetupExchange(invoker, second);

        var token = await provider.GetTokenAsync(ServiceId.Users);

        token.Should().Be(second.Token);
    }

    [Fact]
    public async Task GetTokenAsync_OnRpcFailure_UsesStillValidCache()
    {
        var (invoker, provider, time) = Build();
        SetupExchange(invoker, Response(TimeSpan.FromMinutes(10)));

        var cached = await provider.GetTokenAsync(ServiceId.Users);

        invoker
            .Setup(i => i.AsyncUnaryCall(
                It.IsAny<Method<GetServiceTokenRequest, GetServiceTokenResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>(),
                It.IsAny<GetServiceTokenRequest>()))
            .Returns(Failure<GetServiceTokenResponse>());
        time.Advance(TimeSpan.FromMinutes(10));

        // Кеш формально истёк, но запас 60 секунд ещё действует — работаем на старом токене.
        var token = await provider.GetTokenAsync(ServiceId.Users);

        token.Should().Be(cached);
    }

    [Fact]
    public async Task GetTokenAsync_OnRpcFailureWithoutValidCache_Throws()
    {
        var (invoker, provider, _) = Build();
        invoker
            .Setup(i => i.AsyncUnaryCall(
                It.IsAny<Method<GetServiceTokenRequest, GetServiceTokenResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>(),
                It.IsAny<GetServiceTokenRequest>()))
            .Returns(Failure<GetServiceTokenResponse>());

        var act = () => provider.GetTokenAsync(ServiceId.Users);

        await act.Should().ThrowAsync<RpcException>();
    }
}
