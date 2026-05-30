namespace BarkCloud.Drive.App;

// Разбор полей адреса сервера (общий для мастера и настроек).
internal static class ServerInput
{
    // Хост вводят как «cloud.example.com», но прощаем случайно вставленную схему/слэш.
    public static string StripScheme(string host)
    {
        var h = host.Trim();
        var i = h.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0)
            h = h[(i + 3)..];
        return h.TrimEnd('/').Trim();
    }

    public static bool TryPort(string? text, out int port)
        => int.TryParse(text?.Trim(), out port) && port is >= 1 and <= 65535;
}
