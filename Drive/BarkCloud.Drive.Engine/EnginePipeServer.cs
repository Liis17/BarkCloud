using System.IO.Pipes;

using BarkCloud.Drive.Contracts;

using StreamJsonRpc;

namespace BarkCloud.Drive.Engine;

internal static class EnginePipeServer
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(string pipeName, IDriveEngine engine, CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, EnginePipe.Options);
            try
            {
                await pipe.WaitForConnectionAsync(lifetime);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }

            var rpc = JsonRpc.Attach(pipe, engine);
            try
            {
                await rpc.Completion.WaitAsync(lifetime);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                try { await rpc.Completion.WaitAsync(ShutdownTimeout); }
                catch { /* клиент закрыл pipe или не завершил RPC за время остановки */ }
            }
            catch { /* клиент отключился — ждём следующего */ }
            finally
            {
                rpc.Dispose();
            }
        }
    }
}
