using BarkCloud.GrpcServer.Tests.Logging;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Serilog;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarkCloud.GrpcServer.Tests;

[Collection("Logging lifecycle")]
public class SerilogExtensionsTests
{
    [Fact]
    public void StartupOptions_DefaultToConsoleOnly()
    {
        var builder = WebApplication.CreateBuilder();

        LoggingStartupOptions.Capture(builder).Should().Be(new LoggingStartupOptions(true, false, false));
    }

    [Fact]
    public async Task DisabledLognara_DoesNotRequireAgentBinary()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConsoleOutput"] = "false",
            ["SeqEnable"] = "false",
            ["LognaraEnable"] = "false"
        });

        await using var lifetime = await builder.AddBarkCloudSerilogAsync("BarkCloud.Test");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ConsoleSeqAndLognara_AreIndependent(bool console, bool seq, bool lognara)
    {
        await using var seqEndpoint = await FakeHttpEndpoint.StartAsync();
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-logging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        using var consoleOutput = new StringWriter();
        var originalConsole = Console.Out;
        Console.SetOut(consoleOutput);

        try
        {
            var options = new LoggingStartupOptions(console, seq, lognara);
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                options,
                "BarkCloud.Test",
                seqEndpoint.Address,
                $"{lognaraEndpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            const string marker = "logging-matrix-unique-marker";
            logger.Information("{Marker}", marker);
            await DisposeLoggerAsync(logger);

            Count(consoleOutput.ToString(), marker).Should().Be(console ? 1 : 0);

            SerializedTraceContext? seqTrace = null;
            if (seq)
            {
                var request = await seqEndpoint.ReadAsync(TimeSpan.FromSeconds(3));
                request.Method.Should().Be("POST");
                Count(request.Body, marker).Should().Be(1);
                seqTrace = ReadEvents(request, "@tr", "@sp").Single();
                seqTrace.TraceId.Should().MatchRegex("^[a-f0-9]{32}$");
                seqTrace.SpanId.Should().MatchRegex("^[a-f0-9]{16}$");
            }
            else
            {
                seqEndpoint.Requests.TryRead(out _).Should().BeFalse();
                Directory.GetFiles(temporaryDirectory, "seq-buffer*", SearchOption.AllDirectories).Should().BeEmpty();
            }

            if (lognara)
            {
                var request = await lognaraEndpoint.ReadAsync(TimeSpan.FromSeconds(3));
                request.Method.Should().Be("POST");
                request.Path.Should().Be("/v1/logs");
                using var payload = JsonDocument.Parse(request.Body);
                payload.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
                payload.RootElement.GetArrayLength().Should().Be(1);
                request.Body.Should().Contain(marker);
                var lognaraTrace = ReadEvents(request, "trace_id", "span_id").Single();
                lognaraTrace.TraceId.Should().MatchRegex("^[a-f0-9]{32}$");
                lognaraTrace.SpanId.Should().MatchRegex("^[a-f0-9]{16}$");
                if (seqTrace is not null)
                    lognaraTrace.Should().Be(seqTrace);
                lognaraEndpoint.Requests.TryRead(out _).Should().BeFalse();
                Directory.GetFiles(temporaryDirectory, "*.txt", SearchOption.AllDirectories).Should().BeEmpty();
            }
            else
            {
                lognaraEndpoint.Requests.TryRead(out _).Should().BeFalse();
                Directory.GetFiles(temporaryDirectory, "*.txt", SearchOption.AllDirectories).Should().BeEmpty();
            }
        }
        finally
        {
            Console.SetOut(originalConsole);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FallbackTraceContextIsSharedBySeqLognaraAndConsole()
    {
        await using var seqEndpoint = await FakeHttpEndpoint.StartAsync();
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-trace-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        using var consoleOutput = new StringWriter();
        var originalConsole = Console.Out;
        var previousActivity = Activity.Current;
        Console.SetOut(consoleOutput);
        Activity.Current = null;

        try
        {
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                new LoggingStartupOptions(true, true, true),
                "BarkCloud.Test",
                seqEndpoint.Address,
                $"{lognaraEndpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            logger.Information("fallback-trace-context-marker");
            Activity.Current.Should().BeNull();
            await DisposeLoggerAsync(logger);

            var seqEvent = ReadEvents(await seqEndpoint.ReadAsync(TimeSpan.FromSeconds(3)), "@tr", "@sp").Single();
            var lognaraEvent = ReadEvents(await lognaraEndpoint.ReadAsync(TimeSpan.FromSeconds(3)), "trace_id", "span_id").Single();

            seqEvent.TraceId.Should().NotBeNullOrWhiteSpace().And.HaveLength(32);
            seqEvent.SpanId.Should().NotBeNullOrWhiteSpace().And.HaveLength(16);
            seqEvent.Should().Be(lognaraEvent);
            consoleOutput.ToString().Should().Contain(seqEvent.TraceId!).And.Contain(seqEvent.SpanId!);
        }
        finally
        {
            Activity.Current = previousActivity;
            Console.SetOut(originalConsole);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MicrosoftLoggerExceptionEventCarriesNativeTraceContext()
    {
        await using var seqEndpoint = await FakeHttpEndpoint.StartAsync();
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-logger-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                new LoggingStartupOptions(false, true, true),
                "BarkCloud.Test",
                seqEndpoint.Address,
                $"{lognaraEndpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            using (var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: false)))
            {
                var typedLogger = loggerFactory.CreateLogger<SerilogExtensionsTests>();
                typedLogger.LogError(
                    new InvalidOperationException("logger-exception-detail"),
                    "logger-exception-event {RequestId}",
                    "logger-request-42");
            }

            await DisposeLoggerAsync(logger);

            var seqRequest = await seqEndpoint.ReadAsync(TimeSpan.FromSeconds(3));
            var lognaraRequest = await lognaraEndpoint.ReadAsync(TimeSpan.FromSeconds(3));
            var seqContext = ReadEvents(seqRequest, "@tr", "@sp").Single();
            var lognaraContext = ReadEvents(lognaraRequest, "trace_id", "span_id").Single();

            seqContext.Should().Be(lognaraContext);
            seqContext.TraceId.Should().NotBeNullOrWhiteSpace();
            seqContext.SpanId.Should().NotBeNullOrWhiteSpace();
            seqRequest.Body.Should().Contain("logger-exception-detail").And.Contain("logger-request-42");
            lognaraRequest.Body.Should().Contain("logger-exception-detail").And.Contain("logger-request-42");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MetricsReporterEmitsNativeTraceContextInServiceMetricsPayload()
    {
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-metrics-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var collector = new MetricsCollector();
        collector.Increment("requests.completed");

        var logger = SerilogExtensions.ConfigureLogger(
            new LoggerConfiguration(),
            new LoggingStartupOptions(false, false, true),
            "BarkCloud.Test",
            "http://127.0.0.1:1",
            $"{lognaraEndpoint.Address}/v1/logs",
            Path.Combine(temporaryDirectory, "seq-buffer"))
            .CreateLogger();

        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: false));
            var reporter = new MetricsReporterService(
                collector,
                loggerFactory.CreateLogger<MetricsReporterService>(),
                "BarkCloud.Test");

            await reporter.StartAsync(CancellationToken.None);
            try
            {
                var request = await lognaraEndpoint.ReadAsync(TimeSpan.FromSeconds(8));
                var traceContext = ReadEvents(request, "trace_id", "span_id").Single();

                request.Body.Should().Contain("ServiceMetrics").And.Contain("requests.completed");
                traceContext.TraceId.Should().NotBeNullOrWhiteSpace().And.HaveLength(32);
                traceContext.SpanId.Should().NotBeNullOrWhiteSpace().And.HaveLength(16);
            }
            finally
            {
                await reporter.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await DisposeLoggerAsync(logger);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingActivityTraceContextIsPreservedAcrossSinks()
    {
        await using var seqEndpoint = await FakeHttpEndpoint.StartAsync();
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-existing-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                new LoggingStartupOptions(false, true, true),
                "BarkCloud.Test",
                seqEndpoint.Address,
                $"{lognaraEndpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            using var activity = new Activity("existing-trace-context")
                .SetIdFormat(ActivityIdFormat.W3C)
                .Start();
            logger.Information("existing-trace-context-marker");
            await DisposeLoggerAsync(logger);

            var seqEvent = ReadEvents(await seqEndpoint.ReadAsync(TimeSpan.FromSeconds(3)), "@tr", "@sp").Single();
            var lognaraEvent = ReadEvents(await lognaraEndpoint.ReadAsync(TimeSpan.FromSeconds(3)), "trace_id", "span_id").Single();
            var expected = new SerializedTraceContext(activity.TraceId.ToHexString(), activity.SpanId.ToHexString());

            seqEvent.Should().Be(expected);
            lognaraEvent.Should().Be(expected);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentFallbackEventsGetDistinctPairsSharedByBothSinks()
    {
        const int eventCount = 24;
        await using var seqEndpoint = await FakeHttpEndpoint.StartAsync();
        await using var lognaraEndpoint = await FakeHttpEndpoint.StartAsync();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-concurrent-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var previousActivity = Activity.Current;
        Activity.Current = null;

        try
        {
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                new LoggingStartupOptions(false, true, true),
                "BarkCloud.Test",
                seqEndpoint.Address,
                $"{lognaraEndpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            Parallel.For(0, eventCount, index => logger.Information("concurrent fallback event {EventNumber}", index));
            await DisposeLoggerAsync(logger);

            var seqEvents = await ReadEventsAsync(seqEndpoint, eventCount, "@tr", "@sp");
            var lognaraEvents = await ReadEventsAsync(lognaraEndpoint, eventCount, "trace_id", "span_id");

            seqEvents.Should().OnlyHaveUniqueItems();
            lognaraEvents.Should().OnlyHaveUniqueItems();
            seqEvents.OrderBy(x => x.TraceId).ThenBy(x => x.SpanId)
                .Should().Equal(lognaraEvents.OrderBy(x => x.TraceId).ThenBy(x => x.SpanId));
        }
        finally
        {
            Activity.Current = previousActivity;
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DelayedLognaraResponse_DoesNotBlockPastClientTimeoutOrWriteSinkBuffer()
    {
        await using var endpoint = await FakeHttpEndpoint.StartAsync(TimeSpan.FromSeconds(7));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"barkcloud-lognara-delay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var logger = SerilogExtensions.ConfigureLogger(
                new LoggerConfiguration(),
                new LoggingStartupOptions(false, false, true),
                "BarkCloud.Test",
                "http://127.0.0.1:1",
                $"{endpoint.Address}/v1/logs",
                Path.Combine(temporaryDirectory, "seq-buffer"))
                .CreateLogger();

            logger.Information("delayed-shutdown-marker");
            var stopwatch = Stopwatch.StartNew();
            var shutdown = DisposeLoggerAsync(logger);
            await endpoint.ReadAsync(TimeSpan.FromSeconds(3));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(8));

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
            Directory.GetFiles(temporaryDirectory, "*.txt", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task DisposeLoggerAsync(Serilog.ILogger logger)
    {
        if (logger is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            ((IDisposable)logger).Dispose();
    }

    private static int Count(string value, string marker)
        => Regex.Matches(value, Regex.Escape(marker)).Count;

    private static IEnumerable<SerializedTraceContext> ReadEvents(FakeRequest request, string traceProperty, string spanProperty)
    {
        var traceContexts = new List<SerializedTraceContext>();
        JsonDocument? payload = null;
        try
        {
            payload = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            // Seq uses newline-delimited CLEF records, while Lognara sends a JSON array.
        }

        if (payload is not null)
        {
            using (payload)
                AddRoot(payload.RootElement);
        }
        else
        {
            foreach (var json in request.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var line = JsonDocument.Parse(json);
                AddRoot(line.RootElement);
            }
        }

        return traceContexts;

        void AddRoot(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                AddEvents(root);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Events", out var events))
            {
                AddEvents(events);
            }
            else
            {
                AddEvent(root);
            }
        }

        void AddEvents(JsonElement events)
        {
            foreach (var logEvent in events.EnumerateArray())
                AddEvent(logEvent);
        }

        void AddEvent(JsonElement logEvent)
        {
            traceContexts.Add(new SerializedTraceContext(
                logEvent.GetProperty(traceProperty).GetString(),
                logEvent.GetProperty(spanProperty).GetString()));
        }
    }

    private static async Task<List<SerializedTraceContext>> ReadEventsAsync(
        FakeHttpEndpoint endpoint,
        int expectedCount,
        string traceProperty,
        string spanProperty)
    {
        var events = new List<SerializedTraceContext>(expectedCount);
        while (events.Count < expectedCount)
            events.AddRange(ReadEvents(await endpoint.ReadAsync(TimeSpan.FromSeconds(3)), traceProperty, spanProperty));

        return events;
    }

    private sealed record SerializedTraceContext(string? TraceId, string? SpanId);
}
