using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace AuditRecheck;

public record ProbeMessage(int Id);

public static class Program
{
    public static async Task Main(string[] args)
    {
        var root = args[0];
        foreach (var service in new[] { "Web", "Torrent" })
        {
            var source = File.ReadAllText(Path.Combine(root, $"Backend/BarkCloud.{service}/Program.cs"));
            if (!source.Contains("builder.Services.AddHttpClient(\"files-upload\");"))
                throw new Exception("Client registration changed; update probe.");
            var services = new ServiceCollection();
            services.AddHttpClient("files-upload");
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("files-upload");
            Console.WriteLine($"F17 {service} files-upload TimeoutSeconds={client.Timeout.TotalSeconds}");
        }

        var files = File.ReadAllText(Path.Combine(root, "Backend/BarkCloud.Files/Program.cs"));
        var block = Regex.Match(files, @"e\.ConcurrentMessageLimit = (\d+);\s*e\.UseMessageRetry\(r => r\.Intervals\((.*?)\)\);", RegexOptions.Singleline);
        if (!block.Success) throw new Exception("Retry configuration changed; update probe.");
        var limit = int.Parse(block.Groups[1].Value);
        var intervals = Regex.Matches(block.Groups[2].Value, @"TimeSpan\.From(Seconds|Minutes)\((\d+)\)")
            .Select(m => m.Groups[1].Value == "Seconds" ? TimeSpan.FromSeconds(int.Parse(m.Groups[2].Value)) : TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value)))
            .ToArray();
        var twoFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var bus = Bus.Factory.CreateUsingInMemory(cfg => cfg.ReceiveEndpoint("f18-recheck", e =>
        {
            e.ConcurrentMessageLimit = limit;
            e.UseMessageRetry(r => r.Intervals(intervals));
            e.Handler<ProbeMessage>(context =>
            {
                if (context.Message.Id < 3)
                {
                    if (Interlocked.Increment(ref count) == 2) twoFailed.TrySetResult();
                    throw new InvalidOperationException("Controlled failure");
                }
                healthy.TrySetResult();
                return Task.CompletedTask;
            });
        }));
        await bus.StartAsync();
        var endpoint = await bus.GetSendEndpoint(new Uri(bus.Address, "f18-recheck"));
        await endpoint.Send(new ProbeMessage(1));
        await endpoint.Send(new ProbeMessage(2));
        await twoFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await endpoint.Send(new ProbeMessage(3));
        var processed = await Task.WhenAny(healthy.Task, Task.Delay(1500)) == healthy.Task;
        Console.WriteLine($"F18 limit={limit}; waitSeconds={intervals.Sum(i => i.TotalSeconds)}; healthyProcessedWithin1500ms={processed}");
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try { await bus.StopAsync(stop.Token); }
            catch (OperationCanceledException) { }
        }

        using var browserAbort = new CancellationTokenSource();
        var handler = new DelayHandler();
        using var uncancelled = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var post = uncancelled.PostAsync("http://loopback.invalid/upload", new StringContent("bytes"));
        browserAbort.Cancel();
        await Task.Delay(100);
        Console.WriteLine($"F17 omitted-token browserCancelled={browserAbort.IsCancellationRequested}; postCompleted={post.IsCompleted}; upstreamCancelled={handler.Cancelled}");
        uncancelled.CancelPendingRequests();
        try { await post; } catch (OperationCanceledException) { }

        using var timed = new HttpClient(new DelayHandler());
        var sw = Stopwatch.StartNew();
        try { await timed.PostAsync("http://loopback.invalid/upload", new StringContent("bytes")); }
        catch (TaskCanceledException ex)
        {
            Console.WriteLine($"F17 default timeout elapsedSeconds={sw.Elapsed.TotalSeconds:F1}; innerException={ex.InnerException?.GetType().Name}");
        }
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
