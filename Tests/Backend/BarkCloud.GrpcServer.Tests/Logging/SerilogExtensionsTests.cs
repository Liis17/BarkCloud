using BarkCloud.GrpcServer.Tests.Logging;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

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

            if (seq)
            {
                var request = await seqEndpoint.ReadAsync(TimeSpan.FromSeconds(3));
                request.Method.Should().Be("POST");
                Count(request.Body, marker).Should().Be(1);
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
}
