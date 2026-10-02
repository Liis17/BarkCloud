using BarkCloud.GrpcServer.Tracker;

namespace BarkCloud.Identity.Services;

/// <summary>Общий блок полей писем о действиях пользователя: откуда и с какого устройства. Время — момент события.</summary>
public static class NotificationPayload
{
    public static Dictionary<string, string> Device(RequestContext context, string? location = null)
        => Device(
            context.IpAddress,
            context.DeviceName,
            context.OperationSystem,
            $"{context.AppName} v.{context.AppVersion}",
            location);

    /// <param name="appName">Готовая строка «приложение + версия».</param>
    /// <param name="location">Уже известная геолокация; если не задана, её определит воркер по <c>ip</c>.</param>
    public static Dictionary<string, string> Device(
        string? ipAddress, string? deviceName, string? operationSystem, string appName, string? location = null)
    {
        var payload = new Dictionary<string, string>
        {
            { "ip", ipAddress ?? string.Empty },
            { "devicename", deviceName ?? string.Empty },
            { "os", operationSystem ?? string.Empty },
            { "appname", appName },
            { "datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") }
        };

        if (location is not null)
        {
            payload["location"] = location;
        }

        return payload;
    }
}
