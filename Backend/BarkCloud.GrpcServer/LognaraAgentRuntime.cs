using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace BarkCloud.GrpcServer;

internal interface ILognaraAgentProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    bool SendSigterm();
    void Kill();
}

internal interface ILognaraAgentProcessFactory
{
    ILognaraAgentProcess Start(ProcessStartInfo startInfo);
}

internal sealed class LognaraAgentProcessFactory : ILognaraAgentProcessFactory
{
    public ILognaraAgentProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Process.Start returned false.");
        }

        return new LognaraAgentProcess(process);
    }
}

internal sealed class LognaraAgentProcess(Process process) : ILognaraAgentProcess
{
    public bool HasExited => process.HasExited;
    public int? ExitCode => HasExited ? process.ExitCode : null;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => process.WaitForExitAsync(cancellationToken);

    public bool SendSigterm()
    {
        if (!OperatingSystem.IsLinux())
        {
            process.Kill(entireProcessTree: false);
            return true;
        }

        if (NativeMethods.Kill(process.Id, 15) != 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 3) // ESRCH: процесс завершился между проверкой состояния и отправкой сигнала.
                return false;
            else
                throw new Win32Exception(error);
        }

        return true;
    }

    public void Kill() => process.Kill(entireProcessTree: false);

    public void Dispose() => process.Dispose();

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int processId, int signal);
#pragma warning restore SYSLIB1054
    }
}

internal sealed class LognaraAgentRuntime : IAsyncDisposable
{
    internal const string AgentPath = "/usr/local/bin/lognara-agent";
    internal const string SpoolDirectory = "/var/lib/lognara-agent/spool";
    internal static readonly Uri ReadinessUri = new("http://127.0.0.1:7400/v1/logs");

    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeRequestTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILognaraAgentProcess _process;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _stopTimeout;
    private int _stopped;

    private LognaraAgentRuntime(
        ILognaraAgentProcess process,
        HttpClient httpClient,
        TimeSpan stopTimeout)
    {
        _process = process;
        _httpClient = httpClient;
        _stopTimeout = stopTimeout;
    }

    internal bool HasExited => _process.HasExited;
    internal int? ExitCode => _process.ExitCode;

    internal static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo(AgentPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("LOGNARA_SOURCES_FILE");
        startInfo.Environment["LOGNARA_LISTEN_ADDR"] = "127.0.0.1:7400";
        return startInfo;
    }

    internal static async Task<LognaraAgentRuntime> StartAsync(
        ILognaraAgentProcessFactory? processFactory = null,
        HttpMessageHandler? httpHandler = null,
        TimeSpan? readinessTimeout = null,
        TimeSpan? stopTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var handler = httpHandler ?? new HttpClientHandler { UseProxy = false };
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ProbeRequestTimeout
        };

        ILognaraAgentProcess process;
        try
        {
            process = (processFactory ?? new LognaraAgentProcessFactory()).Start(CreateStartInfo());
        }
        catch (Exception exception)
        {
            httpClient.Dispose();
            throw new InvalidOperationException(
                $"Unable to start the Lognara agent at '{AgentPath}'. Install the agent in the application image.",
                exception);
        }

        var runtime = new LognaraAgentRuntime(
            process,
            httpClient,
            stopTimeout ?? DefaultStopTimeout);

        try
        {
            await runtime.WaitForReadinessAsync(readinessTimeout ?? DefaultReadinessTimeout, cancellationToken);
            return runtime;
        }
        catch (Exception startupError)
        {
            try
            {
                await runtime.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Lognara agent startup and cleanup failed.", startupError, cleanupError);
            }

            throw;
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        => await _process.WaitForExitAsync(cancellationToken);

    internal async Task<bool> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return false;

        _httpClient.Dispose();
        if (_process.HasExited)
        {
            _process.Dispose();
            return true;
        }

        Exception? signalError = null;
        try
        {
            if (!_process.SendSigterm())
            {
                await _process.WaitForExitAsync(CancellationToken.None);
                _process.Dispose();
                return true;
            }
        }
        catch (Exception) when (_process.HasExited)
        {
            _process.Dispose();
            return true;
        }
        catch (Exception exception)
        {
            signalError = exception;
        }

        try
        {
            if (signalError is not null)
            {
                _process.Kill();
                await _process.WaitForExitAsync(CancellationToken.None);
                throw new InvalidOperationException("Could not send SIGTERM to the owned Lognara agent; it was killed.", signalError);
            }

            using var timeout = new CancellationTokenSource(_stopTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _process.Kill();
                await _process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            _process.Dispose();
        }

        return false;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task WaitForReadinessAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The Lognara agent exited before readiness (exit code {_process.ExitCode?.ToString() ?? "unknown"}).");
            }

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(remaining < ProbeRequestTimeout ? remaining : ProbeRequestTimeout);
            try
            {
                using var response = await _httpClient.GetAsync(
                    ReadinessUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed
                    && AllowsPost(response))
                {
                    if (_process.HasExited)
                        throw new InvalidOperationException("The Lognara agent exited during readiness verification.");
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The local endpoint has not become ready yet; retry within the startup deadline.
            }
            catch (HttpRequestException)
            {
                // The process may still be binding its loopback listener.
            }

            remaining = timeout - timer.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining < ProbeInterval ? remaining : ProbeInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"The Lognara agent did not answer GET {ReadinessUri.AbsolutePath} with 405 and Allow: POST within {timeout.TotalSeconds:0} seconds.");
    }

    private static bool AllowsPost(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Allow", out var responseMethods)
            && responseMethods.Contains("POST", StringComparer.OrdinalIgnoreCase))
            return true;

        return response.Content.Headers.TryGetValues("Allow", out var contentMethods)
            && contentMethods.Contains("POST", StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class LognaraAgentMonitorService(
    LognaraAgentRuntime agent,
    IHostApplicationLifetime applicationLifetime,
    ILogger<LognaraAgentMonitorService> logger) : IHostedService
{
    private CancellationTokenSource? _stopping;
    private Task? _monitorTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (agent.HasExited)
        {
            Environment.ExitCode = 1;
            throw new InvalidOperationException(
                $"The Lognara agent exited before host startup (exit code {agent.ExitCode?.ToString() ?? "unknown"}).");
        }

        _stopping = new CancellationTokenSource();
        _monitorTask = MonitorAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping?.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopping?.IsCancellationRequested == true)
            {
                // Host shutdown owns the child after the monitor has been cancelled.
            }
        }
        _stopping?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await agent.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Environment.ExitCode = 1;
            logger.LogCritical("The owned Lognara agent exited unexpectedly with code {ExitCode}.", agent.ExitCode);
            applicationLifetime.StopApplication();
        }
    }
}

public sealed class BarkCloudLoggingLifetime : IAsyncDisposable
{
    internal static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(20);
    private readonly bool _consoleOutput;
    private readonly LognaraAgentRuntime? _agent;
    private readonly ActivityListener? _activityListener;
    private readonly Func<Task> _flushLogger;
    private readonly TimeSpan _flushTimeout;
    private int _disposed;

    internal BarkCloudLoggingLifetime(
        bool consoleOutput,
        LognaraAgentRuntime? agent,
        Func<Task>? flushLogger = null,
        TimeSpan? flushTimeout = null,
        ActivityListener? activityListener = null)
    {
        _consoleOutput = consoleOutput;
        _agent = agent;
        _flushLogger = flushLogger ?? (() => Log.CloseAndFlushAsync().AsTask());
        _flushTimeout = flushTimeout ?? FlushTimeout;
        _activityListener = activityListener;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? shutdownError = null;
        try
        {
            await FlushLoggerAsync(_flushLogger, _flushTimeout);
        }
        catch (Exception exception)
        {
            shutdownError = exception;
        }

        try
        {
            _activityListener?.Dispose();
        }
        catch (Exception exception)
        {
            shutdownError ??= exception;
        }

        try
        {
            if (_agent is not null)
            {
                if (await _agent.StopAsync())
                    shutdownError ??= new InvalidOperationException("The Lognara agent exited before graceful shutdown began.");
            }
        }
        catch (Exception exception)
        {
            shutdownError ??= exception;
        }

        if (shutdownError is not null)
        {
            Environment.ExitCode = 1;
            if (_consoleOutput)
                Console.Error.WriteLine($"[shutdown] BarkCloud logging shutdown failed: {shutdownError.Message}");
        }
    }

    internal static async Task FlushLoggerAsync(Func<Task> flushLogger, TimeSpan timeout)
    {
        var flushTask = flushLogger();
        try
        {
            await flushTask.WaitAsync(timeout);
        }
        catch (TimeoutException exception) when (!flushTask.IsCompleted)
        {
            // Sink HTTP requests are independently bounded (Seq: 10 s, Lognara: 5 s), so close can drain them.
            _ = flushTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException($"Serilog flush/dispose exceeded its {timeout.TotalSeconds:0}-second budget.", exception);
        }
    }
}
