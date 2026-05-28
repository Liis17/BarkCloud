using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Interceptors;

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace BarkCloud.Shared.Exceptions.Tests.Interceptors;

public class ExceptionClientInterceptorTests
{
    private sealed class EmptyMessage { }

    private static readonly Marshaller<EmptyMessage> EmptyMarshaller =
        Marshallers.Create(_ => Array.Empty<byte>(), _ => new EmptyMessage());

    private static readonly Method<EmptyMessage, EmptyMessage> TestMethod =
        new(MethodType.Unary, "TestService", "TestMethod", EmptyMarshaller, EmptyMarshaller);

    private static AsyncUnaryCall<EmptyMessage> Failure(Status status, Metadata trailers)
    {
        var rpcEx = new RpcException(status, trailers);
        return new AsyncUnaryCall<EmptyMessage>(
            Task.FromException<EmptyMessage>(rpcEx),
            Task.FromResult(new Metadata()),
            () => status,
            () => trailers,
            () => { });
    }

    private static async Task<Exception> InvokeAsync(ExceptionClientInterceptor sut, Metadata trailers)
    {
        var context = new ClientInterceptorContext<EmptyMessage, EmptyMessage>(TestMethod, host: null, new CallOptions());
        var call = sut.AsyncUnaryCall<EmptyMessage, EmptyMessage>(
            new EmptyMessage(),
            context,
            (_, _) => Failure(new Status(StatusCode.FailedPrecondition, "fail"), trailers));

        try
        {
            await call.ResponseAsync;
            throw new InvalidOperationException("Expected exception was not thrown.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task AsyncUnary_KnownErrorCode_ThrowsMappedDomainException()
    {
        var sut = new ExceptionClientInterceptor();
        var domain = new InvalidLoginOrPasswordException();
        var trailers = new Metadata { { "x-error-code", domain.ErrorCode } };

        var ex = await InvokeAsync(sut, trailers);

        ex.Should().BeOfType<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task AsyncUnary_UnknownErrorCode_RethrowsRpcException()
    {
        var sut = new ExceptionClientInterceptor();
        var trailers = new Metadata { { "x-error-code", "00000000-0000-0000-0000-000000000000" } };

        var ex = await InvokeAsync(sut, trailers);

        ex.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task AsyncUnary_NoErrorCodeTrailer_RethrowsRpcException()
    {
        var sut = new ExceptionClientInterceptor();
        var trailers = new Metadata();

        var ex = await InvokeAsync(sut, trailers);

        ex.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task AsyncUnary_OtpCodeNeededError_ThrowsOtpCodeNeedException()
    {
        var sut = new ExceptionClientInterceptor();
        var domain = new OtpCodeNeedException();
        var trailers = new Metadata { { "x-error-code", domain.ErrorCode } };

        var ex = await InvokeAsync(sut, trailers);

        ex.Should().BeOfType<OtpCodeNeedException>();
    }
}
