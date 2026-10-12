using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;

using StreamJsonRpc;

namespace BarkCloud.Drive.App;

// Подключение к движку: сначала пробуем connect; если движок не запущен —
// стартуем его процесс и ждём появления pipe.
internal static class EngineLauncher
{
    public static async Task<IDriveEngine> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var proxy = await TryConnectAsync(500, cancellationToken);
        if (proxy != null)
            return proxy;

        cancellationToken.ThrowIfCancellationRequested();
        StartEngine();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            proxy = await TryConnectAsync(500, cancellationToken);
            if (proxy != null)
                return proxy;
            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException(Loc.T("Launcher_ConnectFailed"));
    }

    public static Task<IDriveEngine?> TryConnectToRunningEngineAsync()
        => TryConnectAsync(500);

    private static async Task<IDriveEngine?> TryConnectAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", EnginePipe.Name, PipeDirection.InOut, EnginePipe.Options);
        try
        {
            await pipe.ConnectAsync(timeoutMs, cancellationToken);
            return JsonRpc.Attach<IDriveEngine>(pipe);
        }
        catch
        {
            await pipe.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    // После успешного Shutdown даём процессам текущей установки завершиться самим.
    public static async Task WaitForEngineExitAsync()
    {
        var enginePath = Path.GetFullPath(EnginePath);
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var process in Process.GetProcessesByName("BarkCloud.Drive.Engine"))
        {
            try
            {
                if (process.SessionId != currentSessionId)
                    continue;

                var processPath = process.MainModule?.FileName;
                if (processPath is not null
                    && string.Equals(Path.GetFullPath(processPath), enginePath, StringComparison.OrdinalIgnoreCase))
                    await process.WaitForExitAsync(timeout.Token);
            }
            catch { /* уже завершился / нет доступа / истёк таймаут */ }
            finally { process.Dispose(); }
        }
    }

    // Завершить только движок текущей установки в текущем сеансе (для перезапуска).
    public static async Task KillEngineAsync()
    {
        var enginePath = Path.GetFullPath(EnginePath);
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        foreach (var process in Process.GetProcessesByName("BarkCloud.Drive.Engine"))
        {
            try
            {
                if (process.SessionId != currentSessionId)
                    continue;

                var processPath = process.MainModule?.FileName;
                if (processPath is null
                    || !string.Equals(Path.GetFullPath(processPath), enginePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { /* уже завершился / нет доступа */ }
            finally { process.Dispose(); }
        }
    }

    private static void StartEngine()
    {
        var exe = ResolveEnginePath();
        if (!File.Exists(exe))
            throw new FileNotFoundException(Loc.T("Launcher_EngineNotFoundFmt", exe));

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
    }

    // Путь к exe движка (для записи в автозагрузку).
    public static string EnginePath => ResolveEnginePath();

    private static string ResolveEnginePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. рядом с App (прод / инсталлятор кладёт оба .exe вместе)
        var sideBySide = Path.Combine(baseDir, "BarkCloud.Drive.Engine.exe");
        if (File.Exists(sideBySide))
            return sideBySide;

        // 2. dev-раскладка: соседний проект с тем же Debug/Release + TFM
        var devDir = baseDir.Replace("BarkCloud.Drive.App", "BarkCloud.Drive.Engine");
        return Path.Combine(devDir, "BarkCloud.Drive.Engine.exe");
    }
}
