using Grpc.Core;
using Grpc.Core.Interceptors;

namespace BarkCloud.Shared.Auth.Tests._Helpers;

internal static class InterceptorTestHarness
{
    internal sealed class EmptyMessage { }

    private static readonly Marshaller<EmptyMessage> EmptyMarshaller =
        Marshallers.Create(_ => Array.Empty<byte>(), _ => new EmptyMessage());

    private static readonly Method<EmptyMessage, EmptyMessage> TestMethod =
        new(MethodType.Unary, "TestService", "TestMethod", EmptyMarshaller, EmptyMarshaller);

    public static Metadata CaptureUnaryHeaders(Interceptor interceptor, Metadata? initialHeaders = null)
    {
        var callOptions = initialHeaders is null
            ? new CallOptions()
            : new CallOptions(headers: initialHeaders);
        var context = new ClientInterceptorContext<EmptyMessage, EmptyMessage>(TestMethod, host: null, callOptions);

        Metadata? captured = null;
        AsyncUnaryCallContinuation<EmptyMessage, EmptyMessage> cont = (_, innerCtx) =>
        {
            captured = innerCtx.Options.Headers;
            return new AsyncUnaryCall<EmptyMessage>(
                Task.FromResult(new EmptyMessage()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        };

        _ = interceptor.AsyncUnaryCall(new EmptyMessage(), context, cont);
        return captured ?? new Metadata();
    }
}
