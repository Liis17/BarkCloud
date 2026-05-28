using Grpc.Core;

namespace BarkCloud.TestKit;

public sealed class TestServerCallContext : ServerCallContext
{
    private readonly string _method;
    private readonly Metadata _requestHeaders = new();
    private readonly Metadata _responseTrailers = new();
    private Status _status;
    private WriteOptions? _writeOptions;
    private readonly AuthContext _authContext = new(string.Empty, new Dictionary<string, List<AuthProperty>>());

    public TestServerCallContext(string method = "/Service/Method")
    {
        _method = method;
    }

    protected override string MethodCore => _method;
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test-peer";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }
    protected override AuthContext AuthContextCore => _authContext;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => null!;
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
