using BarkCloud.GrpcServer;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace BarkCloud.GrpcServer.Tests;

[Collection("Logging lifecycle")]
public class LognaraAgentRuntimeTests
{
    [Fact]
    public async Task StartsNoArguments_HidesFileSourcesAndWaitsForHttpReadiness()
    {
        var previousSourceFile = Environment.GetEnvironmentVariable("LOGNARA_SOURCES_FILE");
        Environment.SetEnvironmentVariable("LOGNARA_SOURCES_FILE", "sources.json");
        var processFactory = new FakeProcessFactory();
        using var handler = new ResponseHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri.Should().Be(LognaraAgentRuntime.ReadinessUri);
            var response = new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.TryAddWithoutValidation("Allow", "POST").Should().BeTrue();
            return response;
        });

        try
        {
            var runtime = await LognaraAgentRuntime.StartAsync(
                processFactory, handler, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50));
            var startInfo = processFactory.StartInfo!;

            startInfo.FileName.Should().Be(LognaraAgentRuntime.AgentPath);
            startInfo.ArgumentList.Should().BeEmpty();
            startInfo.UseShellExecute.Should().BeFalse();
            startInfo.RedirectStandardOutput.Should().BeFalse();
            startInfo.RedirectStandardError.Should().BeFalse();
            startInfo.Environment.ContainsKey("LOGNARA_SOURCES_FILE").Should().BeFalse();
            startInfo.Environment["LOGNARA_LISTEN_ADDR"].Should().Be("127.0.0.1:7400");
            handler.RequestCount.Should().Be(1);

            await runtime.DisposeAsync();
            processFactory.Process!.Terminated.Should().BeTrue();
            processFactory.Process.HasExited.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGNARA_SOURCES_FILE", previousSourceFile);
        }
    }

    [Fact]
    public async Task MissingAgentBinary_FailsClearly()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LognaraAgentRuntime.StartAsync(new ThrowingProcessFactory(), readinessTimeout: TimeSpan.FromMilliseconds(20)));

        error.Message.Should().Contain(LognaraAgentRuntime.AgentPath);
    }

    [Fact]
    public async Task WrongHttpReadinessContract_TimesOutAndTerminatesOwnedProcess()
    {
        var processFactory = new FakeProcessFactory();
        using var handler = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.TryAddWithoutValidation("Allow", "GET").Should().BeTrue();
            return response;
        });

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            LognaraAgentRuntime.StartAsync(
                processFactory, handler, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(50)));

        error.Message.Should().Contain("405").And.Contain("Allow: POST");
        processFactory.Process!.Terminated.Should().BeTrue();
        processFactory.Process.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task SlowReadinessProbe_DoesNotExceedStartupDeadline()
    {
        var processFactory = new FakeProcessFactory();
        using var handler = new SlowResponseHandler();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => LognaraAgentRuntime.StartAsync(
            processFactory, handler, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(50)));

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(350));
        processFactory.Process!.Terminated.Should().BeTrue();
        processFactory.Process.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task ChildExitBeforeHttpReadiness_FailsStartupAndDoesNotLeaveAProcess()
    {
        var processFactory = new FakeProcessFactory(exitedAtStart: true);
        using var handler = new ResponseHandler(_ => throw new InvalidOperationException("HTTP should not be reached."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LognaraAgentRuntime.StartAsync(processFactory, handler, TimeSpan.FromSeconds(1)));

        error.Message.Should().Contain("exited before readiness");
        processFactory.Process!.Disposed.Should().BeTrue();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task UnexpectedChildExit_StopsHostAndSetsFailureExitCode()
    {
        var processFactory = new FakeProcessFactory();
        using var handler = ReadyHandler();
        var runtime = await LognaraAgentRuntime.StartAsync(processFactory, handler);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        applicationLifetime.Setup(x => x.StopApplication()).Callback(() => stopped.TrySetResult());
        var service = new LognaraAgentMonitorService(
            runtime, applicationLifetime.Object, NullLogger<LognaraAgentMonitorService>.Instance);
        var previousExitCode = Environment.ExitCode;

        try
        {
            await service.StartAsync(CancellationToken.None);
            processFactory.Process!.Exit(23);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Environment.ExitCode.Should().Be(1);
            applicationLifetime.Verify(x => x.StopApplication(), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await runtime.DisposeAsync();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task LoggingFlushCompletesBeforeAgentReceivesSigterm()
    {
        var processFactory = new FakeProcessFactory();
        using var handler = ReadyHandler();
        var runtime = await LognaraAgentRuntime.StartAsync(processFactory, handler);
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new BarkCloudLoggingLifetime(false, runtime, async () =>
        {
            flushStarted.SetResult();
            await releaseFlush.Task;
        });

        var disposal = lifetime.DisposeAsync().AsTask();
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        processFactory.Process!.Terminated.Should().BeFalse();

        releaseFlush.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));

        processFactory.Process.Terminated.Should().BeTrue();
        processFactory.Process.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task FlushThatExceedsBudget_StopsAgentAndSetsFailureExitCode()
    {
        var processFactory = new FakeProcessFactory();
        using var handler = ReadyHandler();
        var runtime = await LognaraAgentRuntime.StartAsync(processFactory, handler);
        var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousExitCode = Environment.ExitCode;
        var lifetime = new BarkCloudLoggingLifetime(
            false,
            runtime,
            () => releaseFlush.Task,
            TimeSpan.FromMilliseconds(40));

        try
        {
            await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

            processFactory.Process!.Terminated.Should().BeTrue();
            processFactory.Process.Disposed.Should().BeTrue();
            Environment.ExitCode.Should().Be(1);
        }
        finally
        {
            releaseFlush.TrySetResult();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task AgentThatIgnoresSigterm_IsKilledAfterBoundedWait()
    {
        var processFactory = new FakeProcessFactory(exitOnSigterm: false);
        using var handler = ReadyHandler();
        var runtime = await LognaraAgentRuntime.StartAsync(
            processFactory, handler, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40));

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        processFactory.Process!.Terminated.Should().BeTrue();
        processFactory.Process.Killed.Should().BeTrue();
        processFactory.Process.HasExited.Should().BeTrue();
        processFactory.Process.Disposed.Should().BeTrue();
    }

    private static ResponseHandler ReadyHandler()
        => new(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.TryAddWithoutValidation("Allow", "POST").Should().BeTrue();
            return response;
        });

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class SlowResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakeProcessFactory(bool exitOnSigterm = true, bool exitedAtStart = false)
        : ILognaraAgentProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public FakeProcess? Process { get; private set; }

        public ILognaraAgentProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            Process = new FakeProcess(exitOnSigterm, exitedAtStart);
            return Process;
        }
    }

    private sealed class ThrowingProcessFactory : ILognaraAgentProcessFactory
    {
        public ILognaraAgentProcess Start(ProcessStartInfo startInfo)
            => throw new Win32Exception("executable missing");
    }

    private sealed class FakeProcess : ILognaraAgentProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _exitOnSigterm;
        private int? _exitCode;

        public FakeProcess(bool exitOnSigterm, bool exitedAtStart)
        {
            _exitOnSigterm = exitOnSigterm;
            if (exitedAtStart)
            {
                _exitCode = 0;
                _exited.SetResult();
            }
        }

        public bool HasExited => _exited.Task.IsCompleted;
        public int? ExitCode => _exitCode;
        public bool Terminated { get; private set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exited.Task.WaitAsync(cancellationToken);

        public bool SendSigterm()
        {
            Terminated = true;
            if (_exitOnSigterm)
                Exit(0);
            return true;
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public void Exit(int exitCode)
        {
            _exitCode = exitCode;
            _exited.TrySetResult();
        }

        public void Dispose() => Disposed = true;
    }
}
