using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Domain;

public sealed record SettingsScope(ServiceId ServiceId, string EntityName, string TableName);

public static class SettingsScopes
{
    public static IReadOnlyList<SettingsScope> All { get; } =
    [
        new(ServiceId.Unknown, "GlobalSettings", "GlobalSettings"),
        new(ServiceId.Identity, "IdentitySettings", "IdentitySettings"),
        new(ServiceId.Users, "UsersSettings", "UsersSettings"),
        new(ServiceId.Notification, "NotificationSettings", "NotificationSettings"),
        new(ServiceId.Files, "FilesSettings", "FilesSettings"),
        new(ServiceId.Web, "WebSettings", "WebSettings"),
        new(ServiceId.Torrent, "TorrentSettings", "TorrentSettings")
    ];

    public static SettingsScope Get(ServiceId serviceId) =>
        All.FirstOrDefault(scope => scope.ServiceId == serviceId)
        ?? throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown settings scope.");

    public static bool TryGet(ServiceId serviceId, out SettingsScope scope)
    {
        scope = All.FirstOrDefault(item => item.ServiceId == serviceId)!;
        return scope is not null;
    }

    public static bool TryGet(string tableName, out SettingsScope scope)
    {
        scope = All.FirstOrDefault(item => string.Equals(item.TableName, tableName, StringComparison.Ordinal))!;
        return scope is not null;
    }
}
