using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Catalog;

public enum SettingValueKind
{
    String,
    Boolean,
    Integer,
    Url,
    Password
}

public sealed record SettingsCatalogEntry(
    ServiceId ServiceId,
    string Section,
    string Key,
    string StorageKey,
    SettingValueKind ValueKind,
    bool IsSensitive,
    bool IsEnvironmentManaged,
    bool IsComputed,
    IReadOnlyList<string> RestartTargets);

public sealed class UnknownSettingException : InvalidOperationException
{
    public UnknownSettingException(ServiceId serviceId, string section, string key)
        : base($"Unknown settings key [{serviceId}] {section}:{key}.") { }
}

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsCatalogEntry> All { get; } = Build();

    private static readonly IReadOnlyDictionary<(ServiceId, string, string), SettingsCatalogEntry> ByLegacy =
        All.ToDictionary(entry => (entry.ServiceId, entry.Section, entry.Key));

    private static readonly IReadOnlyDictionary<(ServiceId, string), SettingsCatalogEntry> ByStorage =
        All.ToDictionary(entry => (entry.ServiceId, entry.StorageKey));

    public static SettingsCatalogEntry Resolve(ServiceId serviceId, string section, string key)
    {
        if (ByLegacy.TryGetValue((serviceId, section, key), out var entry))
            return entry;
        if (serviceId != ServiceId.Unknown
            && ByLegacy.TryGetValue((ServiceId.Unknown, section, key), out var global))
        {
            return global with
            {
                ServiceId = serviceId,
                RestartTargets = RestartTargets(serviceId)
            };
        }
        throw new UnknownSettingException(serviceId, section, key);
    }

    public static SettingsCatalogEntry Resolve(ServiceId serviceId, string storageKey)
    {
        if (ByStorage.TryGetValue((serviceId, storageKey), out var entry))
            return entry;
        if (serviceId != ServiceId.Unknown
            && ByStorage.TryGetValue((ServiceId.Unknown, storageKey), out var global))
        {
            return global with
            {
                ServiceId = serviceId,
                RestartTargets = RestartTargets(serviceId)
            };
        }
        throw new UnknownSettingException(serviceId, storageKey, string.Empty);
    }

    private static IReadOnlyList<SettingsCatalogEntry> Build()
    {
        var entries = new List<SettingsCatalogEntry>();

        Add(entries, ServiceId.Unknown, "JwtSettings", "SecretKey", SettingValueKind.Password, true);
        Add(entries, ServiceId.Unknown, "JwtSettings", "Issuer");
        Add(entries, ServiceId.Unknown, "JwtSettings", "Audience");
        Add(entries, ServiceId.Unknown, "JwtSettings", "ExpiryMinutes", SettingValueKind.Integer);
        Add(entries, ServiceId.Unknown, "RabbitMQ", "Host");
        Add(entries, ServiceId.Unknown, "RabbitMQ", "Username");
        Add(entries, ServiceId.Unknown, "RabbitMQ", "Password", SettingValueKind.Password, true);
        Add(entries, ServiceId.Unknown, "RabbitMQ", "VirtualHost");
        Add(entries, ServiceId.Unknown, "Seq", "ServerUrl", SettingValueKind.Url);
        Add(entries, ServiceId.Unknown, "Features", "RegistrationEnabled", SettingValueKind.Boolean,
            restartTargets: []);
        Add(entries, ServiceId.Unknown, "Features", "EmailEnabled", SettingValueKind.Boolean,
            environmentManaged: true, computed: true);

        AddService(entries, ServiceId.Identity,
            ("RunSettings", "Port", SettingValueKind.Integer, false, true),
            ("IdentityDb", "", SettingValueKind.Password, true, true),
            ("UsersService", "Host", SettingValueKind.Url, false, false),
            ("UsersService", "Token", SettingValueKind.Password, true, false),
            ("ExternalEndpoint", "Host", SettingValueKind.Url, false, false),
            ("WebAuthn", "RpId", SettingValueKind.String, false, false),
            ("WebAuthn", "ServerName", SettingValueKind.String, false, false),
            ("WebAuthn", "Origins", SettingValueKind.String, false, false));

        AddService(entries, ServiceId.Notification,
            ("RunSettings", "Port", SettingValueKind.Integer, false, true));
        Add(entries, ServiceId.Notification, "Email", "Host",
            restartTargets: ["notification", "identity", "web"]);
        Add(entries, ServiceId.Notification, "Email", "Port", SettingValueKind.Integer,
            restartTargets: ["notification", "identity", "web"]);
        Add(entries, ServiceId.Notification, "Email", "SenderEmail",
            restartTargets: ["notification", "identity", "web"]);
        Add(entries, ServiceId.Notification, "Email", "SenderPassword", SettingValueKind.Password, true,
            restartTargets: ["notification", "identity", "web"]);

        AddService(entries, ServiceId.Users,
            ("RunSettings", "Port", SettingValueKind.Integer, false, true),
            ("UsersDb", "", SettingValueKind.Password, true, true),
            ("FilesService", "Host", SettingValueKind.Url, false, false),
            ("FilesService", "Token", SettingValueKind.Password, true, false),
            ("ExternalEndpoint", "Host", SettingValueKind.Url, false, false));

        AddService(entries, ServiceId.Files,
            ("RunSettings", "Port", SettingValueKind.Integer, false, true),
            ("RunSettings", "Http1Port", SettingValueKind.Integer, false, true),
            ("FilesDb", "", SettingValueKind.Password, true, true),
            ("UsersService", "Host", SettingValueKind.Url, false, false),
            ("UsersService", "Token", SettingValueKind.Password, true, false),
            ("ExternalEndpoint", "Host", SettingValueKind.Url, false, false),
            ("TempFiles", "ExpiresAt", SettingValueKind.Integer, false, false));

        AddService(entries, ServiceId.Torrent,
            ("RunSettings", "Port", SettingValueKind.Integer, false, true),
            ("RunSettings", "Http1Port", SettingValueKind.Integer, false, true),
            ("TorrentDb", "", SettingValueKind.Password, true, true),
            ("UsersService", "Host", SettingValueKind.Url, false, false),
            ("UsersService", "Token", SettingValueKind.Password, true, false),
            ("FilesService", "Host", SettingValueKind.Url, false, false),
            ("FilesService", "Token", SettingValueKind.Password, true, false),
            ("ExternalEndpoint", "Host", SettingValueKind.Url, false, false),
            ("Torrent", "DownloadPath", SettingValueKind.String, false, true),
            ("Torrent", "PeerPort", SettingValueKind.Integer, false, true));

        AddService(entries, ServiceId.Web,
            ("IdentityService", "Host", SettingValueKind.Url, false, false),
            ("UsersService", "Host", SettingValueKind.Url, false, false),
            ("FilesService", "Host", SettingValueKind.Url, false, false),
            ("TorrentService", "Host", SettingValueKind.Url, false, false));

        return entries;
    }

    private static void AddService(
        List<SettingsCatalogEntry> entries,
        ServiceId serviceId,
        params (string Section, string Key, SettingValueKind Kind, bool Sensitive, bool EnvironmentManaged)[] settings)
    {
        foreach (var setting in settings)
            Add(entries, serviceId, setting.Section, setting.Key, setting.Kind, setting.Sensitive, setting.EnvironmentManaged);
    }

    private static void Add(
        List<SettingsCatalogEntry> entries,
        ServiceId serviceId,
        string section,
        string key,
        SettingValueKind kind = SettingValueKind.String,
        bool sensitive = false,
        bool environmentManaged = false,
        bool computed = false,
        IReadOnlyList<string>? restartTargets = null)
    {
        entries.Add(new SettingsCatalogEntry(
            serviceId,
            section,
            key,
            string.IsNullOrEmpty(key) ? section : $"{section}:{key}",
            kind,
            sensitive,
            environmentManaged,
            computed,
            restartTargets ?? RestartTargets(serviceId)));
    }

    private static string[] RestartTargets(ServiceId serviceId) => serviceId switch
    {
        ServiceId.Unknown => ["identity", "users", "notification", "files", "torrent", "web"],
        ServiceId.Identity => ["identity"],
        ServiceId.Users => ["users"],
        ServiceId.Notification => ["notification"],
        ServiceId.Files => ["files"],
        ServiceId.Torrent => ["torrent"],
        ServiceId.Web => ["web"],
        _ => []
    };
}
