using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

using BarkCloud.Drive.Contracts;

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

        throw new InvalidOperationException("не удалось подключиться к движку");
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

    private static void StartEngine()
    {
        var exe = ResolveEnginePath();
        if (!File.Exists(exe))
            throw new FileNotFoundException($"не найден движок: {exe}");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
    }

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
