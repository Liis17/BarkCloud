using System.Collections.Concurrent;
using System.Net;

namespace BarkCloud.Drive.Engine.Tests.Fakes;

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<FakeHttpCall> _calls = new();
    private long _order;

    public FakeHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null)
    {
        Responder = responder ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)));
    }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; }

    public IReadOnlyList<FakeHttpCall> Calls => _calls.OrderBy(call => call.Order).ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(headers, request.Headers);
        if (request.Content is not null)
            AddHeaders(headers, request.Content.Headers);

        _calls.Enqueue(new FakeHttpCall(
            Interlocked.Increment(ref _order),
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            request.Headers.Range?.ToString() ?? GetHeader(headers, "Range"),
            request.Content?.Headers.ContentRange?.ToString() ?? GetHeader(headers, "Content-Range"),
            headers));

        return Responder(request, cancellationToken);
    }

    private static void AddHeaders(IDictionary<string, string> target, System.Net.Http.Headers.HttpHeaders source)
    {
        foreach (var header in source)
            target[header.Key] = string.Join(", ", header.Value);
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed record FakeHttpCall(
    long Order,
    string Method,
    string Url,
    string? Range,
    string? ContentRange,
    IReadOnlyDictionary<string, string> Headers);
