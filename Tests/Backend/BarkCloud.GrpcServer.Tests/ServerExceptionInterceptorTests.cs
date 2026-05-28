using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.TestKit;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.GrpcServer.Tests;

public class ServerExceptionInterceptorTests
{
    private static ServerExceptionInterceptor CreateSut(MetricsCollector? metrics = null)
        => new(NullLogger<ServerExceptionInterceptor>.Instance, metrics);

    [Fact]
    public async Task UnaryServerHandler_SuccessPath_ReturnsContinuationResult()
    {
        var sut = CreateSut();
        var ctx = new TestServerCallContext();

        var result = await sut.UnaryServerHandler<string, string>(
            "req",
            ctx,
            (_, _) => Task.FromResult("response"));

        result.Should().Be("response");
    }

    [Fact]
    public async Task UnaryServerHandler_BaseGrpcException_ThrowsRpcWithErrorCodeTrailer()
    {
        var sut = CreateSut();
        var ctx = new TestServerCallContext();
        var domain = new InvalidLoginOrPasswordException();

        var act = async () => await sut.UnaryServerHandler<string, string>(
            "req",
            ctx,
            (_, _) => throw domain);

        var rpc = (await act.Should().ThrowAsync<RpcException>()).Which;
        rpc.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        rpc.Trailers.Should().Contain(t => t.Key == "x-error-code" && t.Value == domain.ErrorCode);
    }

    [Fact]
    public async Task UnaryServerHandler_UnknownException_ThrowsRpcWithBaseErrorCode()
    {
        var sut = CreateSut();
        var ctx = new TestServerCallContext();

        var act = async () => await sut.UnaryServerHandler<string, string>(
            "req",
            ctx,
            (_, _) => throw new InvalidOperationException("boom"));

        var rpc = (await act.Should().ThrowAsync<RpcException>()).Which;
        rpc.StatusCode.Should().Be(StatusCode.Unknown);
        rpc.Trailers.Should().Contain(t => t.Key == "x-error-code" && t.Value == new BaseGrpcException().ErrorCode);
    }

    [Fact]
    public async Task UnaryServerHandler_SuccessPath_IncrementsRequestsAndDuration()
    {
        var metrics = new MetricsCollector();
        var sut = CreateSut(metrics);
        var ctx = new TestServerCallContext();

        await sut.UnaryServerHandler<string, string>("req", ctx, (_, _) => Task.FromResult("ok"));

        var snap = metrics.SnapshotAndReset();
        snap.Should().ContainKey("grpc_requests_total").WhoseValue.Should().Be(1);
        snap.Should().ContainKey("grpc_request_duration_ms_total");
    }

    [Fact]
    public async Task UnaryServerHandler_BusinessException_IncrementsFailedCounter()
    {
        var metrics = new MetricsCollector();
        var sut = CreateSut(metrics);
        var ctx = new TestServerCallContext();

        var act = async () => await sut.UnaryServerHandler<string, string>(
            "req",
            ctx,
            (_, _) => throw new InvalidLoginOrPasswordException());

        await act.Should().ThrowAsync<RpcException>();
        metrics.SnapshotAndReset()["grpc_requests_failed"].Should().Be(1);
    }

    [Fact]
    public async Task UnaryServerHandler_UnknownException_IncrementsErrorsCounter()
    {
        var metrics = new MetricsCollector();
        var sut = CreateSut(metrics);
        var ctx = new TestServerCallContext();

        var act = async () => await sut.UnaryServerHandler<string, string>(
            "req",
            ctx,
            (_, _) => throw new InvalidOperationException());

        await act.Should().ThrowAsync<RpcException>();
        metrics.SnapshotAndReset()["grpc_requests_errors"].Should().Be(1);
    }
}
