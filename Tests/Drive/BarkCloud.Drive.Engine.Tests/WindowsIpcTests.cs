using System.IO.Pipes;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Engine;

using Moq;
using StreamJsonRpc;
using Xunit;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class WindowsIpcTests
{
    [WindowsFact]
    [Trait("Category", "WindowsIpc")]
    public async Task Named_pipe_completes_call_and_shutdown_response_before_server_stops()
    {
        using var lifetime = new CancellationTokenSource();
        var engine = new Mock<IDriveEngine>();
        engine.Setup(target => target.SetLanguageAsync("en")).Returns(Task.CompletedTask);
        engine.Setup(target => target.ShutdownAsync()).Returns(() =>
        {
            lifetime.Cancel();
            return Task.CompletedTask;
        });
        var pipeName = $"{EnginePipe.Name}.{Guid.NewGuid():N}";
        var server = EnginePipeServer.RunAsync(pipeName, engine.Object, lifetime.Token);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, EnginePipe.Options);
            await pipe.ConnectAsync(5000);
            var proxy = JsonRpc.Attach<IDriveEngine>(pipe);
            try
            {
                await proxy.SetLanguageAsync("en").WaitAsync(TimeSpan.FromSeconds(5));
                await proxy.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                engine.Verify(target => target.SetLanguageAsync("en"), Times.Once);
                engine.Verify(target => target.ShutdownAsync(), Times.Once);
            }
            finally
            {
                ((IDisposable)proxy).Dispose();
            }

            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            lifetime.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Named pipe integration tests require Windows.";
        }
    }
}
