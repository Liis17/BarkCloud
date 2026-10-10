using System.Collections.Concurrent;

using Grpc.Core;

namespace BarkCloud.Drive.Engine.Tests.Fakes;

internal sealed class FakeCallInvoker : CallInvoker
{
    private readonly ConcurrentDictionary<string, Func<object, object>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Exception>> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Exception>> _lostResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeRpcCall> _calls = new();
    private long _order;

    public IReadOnlyList<FakeRpcCall> Calls => _calls.OrderBy(call => call.Order).ToArray();

    public void Handle<TRequest, TResponse>(string methodFullName, Func<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class
        => _handlers[methodFullName] = request => handler((TRequest)request);

    public void FailNext(string methodFullName, Exception exception)
        => _failures.GetOrAdd(methodFullName, _ => new ConcurrentQueue<Exception>()).Enqueue(exception);

    public void LoseNextResponseAfterEffect(string methodFullName)
        => _lostResponses.GetOrAdd(methodFullName, _ => new ConcurrentQueue<Exception>())
            .Enqueue(new RpcException(new Status(StatusCode.Unavailable, "Ответ потерян после выполнения операции")));

    public static RpcException BusinessError(string code)
    {
        var trailers = new Metadata { { "x-error-code", code } };
        return new RpcException(new Status(StatusCode.FailedPrecondition, "Операция отклонена сервером"), trailers);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => Invoke(method, request, options);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        Task<TResponse> response;
        try
        {
            response = Task.FromResult(Invoke(method, request, options));
        }
        catch (Exception ex)
        {
            response = Task.FromException<TResponse>(ex);
        }

        return new AsyncUnaryCall<TResponse>(
            response,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw Unsupported(method);

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => throw Unsupported(method);

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw Unsupported(method);

    private TResponse Invoke<TRequest, TResponse>(
        Method<TRequest, TResponse> method, TRequest request, CallOptions options)
        where TRequest : class
        where TResponse : class
    {
        var fullName = method.FullName;
        _calls.Enqueue(new FakeRpcCall(Interlocked.Increment(ref _order), fullName, request, options.Deadline));

        if (TryTake(_failures, fullName, out var failure))
            throw failure;
        if (!_handlers.TryGetValue(fullName, out var handler))
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Нет обработчика для {fullName}"));

        var response = (TResponse)handler(request);
        if (TryTake(_lostResponses, fullName, out var lostResponse))
            throw lostResponse;

        return response;
    }

    private static bool TryTake(
        ConcurrentDictionary<string, ConcurrentQueue<Exception>> queues,
        string methodFullName,
        out Exception exception)
    {
        if (queues.TryGetValue(methodFullName, out var queue) && queue.TryDequeue(out var queued))
        {
            exception = queued;
            return true;
        }

        exception = null!;
        return false;
    }

    private static NotSupportedException Unsupported<TRequest, TResponse>(Method<TRequest, TResponse> method)
        => new($"Потоковый вызов {method.FullName} не поддержан FakeCallInvoker");
}

internal sealed record FakeRpcCall(long Order, string MethodFullName, object Request, DateTime? Deadline);
