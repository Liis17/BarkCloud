using Microsoft.Win32;

namespace BarkCloud.Drive.App;

// Автозагрузка через HKCU\...\Run (per-user, без прав администратора).
// UI стартует с --tray (сворачивается в трей). Движок — отдельной записью.
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValue = "BarkCloud Drive";
    private const string EngineValue = "BarkCloud Drive Engine";

    public static bool IsAppEnabled() => Has(AppValue);
    public static bool IsEngineEnabled() => Has(EngineValue);

    public static void SetApp(bool enabled)
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
            Set(AppValue, enabled, $"\"{path}\" --tray");
    }

    public static void SetEngine(bool enabled)
    {
        var path = EngineLauncher.EnginePath;
        if (!string.IsNullOrEmpty(path))
            Set(EngineValue, enabled, $"\"{path}\"");
    }

    private static bool Has(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) != null;
    }

    private static void Set(string name, bool enabled, string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
            return;

        if (enabled)
            key.SetValue(name, command);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }
}
