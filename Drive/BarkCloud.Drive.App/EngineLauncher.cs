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
    private const string PipeName = "BarkCloud.Drive.Engine";

    public static async Task<IDriveEngine> ConnectAsync()
    {
        var proxy = await TryConnectAsync(500);
        if (proxy != null)
            return proxy;

        StartEngine();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            proxy = await TryConnectAsync(500);
            if (proxy != null)
                return proxy;
            await Task.Delay(500);
        }

        throw new InvalidOperationException(Loc.T("Launcher_ConnectFailed"));
    }

    private static async Task<IDriveEngine?> TryConnectAsync(int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs);
            return JsonRpc.Attach<IDriveEngine>(pipe);
        }
        catch
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    // Принудительно завершить все процессы движка (для перезапуска).
    public static void KillEngine()
    {
        foreach (var process in Process.GetProcessesByName("BarkCloud.Drive.Engine"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(2000);
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
