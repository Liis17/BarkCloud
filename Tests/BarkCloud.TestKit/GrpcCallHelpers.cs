using Grpc.Core;

namespace BarkCloud.TestKit;

public static class GrpcCallHelpers
{
    public static AsyncUnaryCall<T> AsyncUnary<T>(T response) where T : class
        => new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
}
