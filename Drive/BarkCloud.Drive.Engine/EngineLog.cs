using System.Diagnostics;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

// Лёгкий логгер движка: в отладчик (Debug.WriteLine) и в файл
// %LOCALAPPDATA%\BarkCloud.Drive\engine.log. Движок — WinExe без консоли,
// поэтому файл — основной канал диагностики.
internal static class EngineLog
{
    private static readonly object Gate = new();
    private static readonly string LogFile;

    static EngineLog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BarkCloud.Drive");
        Directory.CreateDirectory(dir);
        LogFile = Path.Combine(dir, "engine.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string operation, Exception ex) => Write("ERROR", $"{operation} — {Describe(ex)}");

    private static string Describe(Exception ex) => ex switch
    {
        RpcException rpc => $"gRPC {rpc.StatusCode}: {rpc.Status.Detail}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
                File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch
        {
            // логирование не должно ломать работу
        }
    }
}
