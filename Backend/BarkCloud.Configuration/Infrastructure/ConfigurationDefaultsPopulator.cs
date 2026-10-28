using BarkCloud.Configuration.Catalog;
using BarkCloud.Configuration.Domain;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Configuration.Infrastructure;

/// <summary>
/// Creates catalog rows, fills empty defaults, and refreshes deployment-managed service credentials.
/// </summary>
public class ConfigurationDefaultsPopulator
{
    private readonly ConfigurationContext _context;
    private readonly ILogger<ConfigurationDefaultsPopulator> _logger;
    private readonly MetricsCollector _metrics;
    private readonly string _postgresHost;
    private readonly int _postgresPort;
    private readonly IReadOnlyDictionary<ServiceId, string> _databasePasswords;
    private readonly string _rabbitHost;
    private readonly string _rabbitVirtualHost;
    private readonly IReadOnlyDictionary<ServiceId, string> _rabbitPasswords;
    private readonly string _minioHost;
    private readonly string _minioPort;
    private readonly string _minioAccessKey;
    private readonly string _minioSecretKey;
    private readonly string _emailHost;
    private readonly string _emailPort;
    private readonly string _emailSenderEmail;
    private readonly string _emailSenderPassword;
    private readonly string _externalIdentityHost;
    private readonly string _externalUsersHost;
    private readonly string _externalFilesHost;
    private readonly string _externalTorrentHost;
    private readonly bool _requireExternalEndpoints;

    private static readonly IReadOnlyDictionary<ServiceId, string> ContainerNames =
        new Dictionary<ServiceId, string>
        {
            [ServiceId.Identity] = "cloud-identity",
            [ServiceId.Users] = "cloud-users",
            [ServiceId.Files] = "cloud-files",
            [ServiceId.Torrent] = "cloud-torrent"
        };

    private static readonly IReadOnlyDictionary<ServiceId, (string? EnvName, int Fallback)> ServicePorts =
        new Dictionary<ServiceId, (string?, int)>
        {
            [ServiceId.Identity] = ("IDENTITY_PORT", 7020),
            [ServiceId.Users] = ("USERS_PORT", 7021),
            [ServiceId.Notification] = (null, 7022),
            [ServiceId.Files] = ("FILES_PORT", 7025),
            [ServiceId.Torrent] = ("TORRENT_PORT", 7027)
        };

    private static readonly IReadOnlyDictionary<ServiceId, (string Section, string Database, string Username)> DatabaseNames =
        new Dictionary<ServiceId, (string, string, string)>
        {
            [ServiceId.Identity] = ("IdentityDb", "identity", "barkcloud_identity"),
            [ServiceId.Users] = ("UsersDb", "users", "barkcloud_users"),
            [ServiceId.Files] = ("FilesDb", "files", "barkcloud_files"),
            [ServiceId.Torrent] = ("TorrentDb", "torrent", "barkcloud_torrent")
        };

    public ConfigurationDefaultsPopulator(
        ConfigurationContext context,
        ILogger<ConfigurationDefaultsPopulator> logger,
        string postgresHost,
        int postgresPort,
        IReadOnlyDictionary<ServiceId, string> databasePasswords,
        string rabbitHost,
        string rabbitVirtualHost,
        IReadOnlyDictionary<ServiceId, string> rabbitPasswords,
        string minioHost,
        string minioPort,
        string minioAccessKey,
        string minioSecretKey,
        string emailHost,
        string emailPort,
        string emailSenderEmail,
        string emailSenderPassword,
        string externalIdentityHost,
        string externalUsersHost,
        string externalFilesHost,
        string externalTorrentHost,
        bool requireExternalEndpoints,
        MetricsCollector? metrics = null)
    {
        _context = context;
        _logger = logger;
        _metrics = metrics ?? new MetricsCollector();
        _postgresHost = postgresHost;
        _postgresPort = postgresPort;
        _databasePasswords = databasePasswords;
        _rabbitHost = rabbitHost;
        _rabbitVirtualHost = rabbitVirtualHost;
        _rabbitPasswords = rabbitPasswords;
        _minioHost = minioHost;
        _minioPort = minioPort;
        _minioAccessKey = minioAccessKey;
        _minioSecretKey = minioSecretKey;
        _emailHost = emailHost;
        _emailPort = emailPort;
        _emailSenderEmail = emailSenderEmail;
        _emailSenderPassword = emailSenderPassword;
        _externalIdentityHost = externalIdentityHost;
        _externalUsersHost = externalUsersHost;
        _externalFilesHost = externalFilesHost;
        _externalTorrentHost = externalTorrentHost;
        _requireExternalEndpoints = requireExternalEndpoints;
    }

    public async Task EnsureSeedAsync()
    {
        var before = await CountSettingsAsync();
        var storage = new ConfigurationStorage(_context, _metrics);
        await storage.SeedMissingCatalogRowsAsync(_ => null);
        var added = await CountSettingsAsync() - before;
        if (added > 0)
            _metrics.Add("configurations_seeded_total", added);
        _logger.LogInformation("Добавлено недостающих записей конфигурации: {Count}", added);
    }

    public async Task PopulateDefaultsAsync()
    {
        ValidateServiceCredentials();
        var global = SettingsScopes.Get(ServiceId.Unknown);
        var globalRows = await _context.Settings(global).AsNoTracking()
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var jwtIssuer = GetNonEmpty(globalRows, "JwtSettings:Issuer") ?? "BarkCloud";
        var jwtAudience = GetNonEmpty(globalRows, "JwtSettings:Audience") ?? "BarkCloudMicroservices";

        ValidateRequiredExternalEndpoints();
        // New service addresses must agree with existing deployment ports during upgrades.
        var servicePorts = new Dictionary<ServiceId, int>();
        foreach (var (serviceId, setting) in ServicePorts)
        {
            var persistedPort = await _context.Settings(SettingsScopes.Get(serviceId))
                .Where(row => row.Key == "RunSettings:Port")
                .Select(row => row.Value).FirstOrDefaultAsync();
            var fallback = int.TryParse(persistedPort, out var port) && port is > 0 and <= 65535
                ? port : setting.Fallback;
            servicePorts[serviceId] = setting.EnvName is null ? fallback : ResolvePort(setting.EnvName, fallback);
        }
        var beforeEmpty = await CountEmptySettingsAsync();
        var storage = new ConfigurationStorage(_context, _metrics);
        await storage.SeedMissingCatalogRowsAsync(entry =>
            ResolveDefault(entry, jwtIssuer, jwtAudience, servicePorts));
        // These credentials are deployment-managed: a password rotation must also refresh
        // previously seeded rows, while operator-editable settings retain their values.
        foreach (var group in SettingsCatalog.All.Where(entry =>
                     entry.IsEnvironmentManaged &&
                     (entry.Section == "RabbitMQ" ||
                      (DatabaseNames.TryGetValue(entry.ServiceId, out var database) && entry.Section == database.Section)))
                 .GroupBy(entry => entry.ServiceId))
        {
            var entries = group.ToDictionary(entry => entry.StorageKey);
            var keys = entries.Keys.ToArray();
            var rows = await _context.Settings(SettingsScopes.Get(group.Key))
                .Where(row => keys.Contains(row.Key)).ToListAsync();
            foreach (var row in rows)
            {
                var value = ResolveDefault(entries[row.Key], jwtIssuer, jwtAudience, servicePorts);
                if (value is null || value == row.Value)
                    continue;
                row.Value = value;
                row.EditedAt = DateTime.UtcNow;
                row.EditedBy = "system";
            }
        }
        await _context.SaveChangesAsync();
        var afterEmpty = await CountEmptySettingsAsync();
        var populated = beforeEmpty - afterEmpty;
        _metrics.Add("defaults_populated_total", populated);
        _metrics.Set("configurations_empty_at_startup", afterEmpty);

        await SeedUniversalProfileAsync();
        _logger.LogInformation("Авто-заполнение завершено. Заполнено: {Count}", populated);
    }

    private string? ResolveDefault(
        SettingsCatalogEntry entry,
        string jwtIssuer,
        string jwtAudience,
        IReadOnlyDictionary<ServiceId, int> servicePorts)
    {
        if (entry.Section == "RunSettings" && entry.Key == "Port")
            return servicePorts[entry.ServiceId].ToString();
        if (entry.Section == "RunSettings" && entry.Key == "Http1Port")
            return entry.ServiceId == ServiceId.Files ? ResolvePort("FILES_HTTP1PORT", 7026).ToString()
                : ResolvePort("TORRENT_HTTP1PORT", 7028).ToString();

        if (entry.Section == "JwtSettings")
            return entry.Key switch
            {
                "Issuer" => jwtIssuer,
                "Audience" => jwtAudience,
                "ExpiryMinutes" => "60",
                _ => null
            };

        if (entry.Section == "RabbitMQ")
        {
            if (entry.Key == "Host")
                return EmptyAsNull(_rabbitHost) ?? "cloud-rabbitmq";
            if (entry.Key == "VirtualHost")
                return EmptyAsNull(_rabbitVirtualHost) ?? "/";
            if (entry.ServiceId != ServiceId.Unknown && entry.Key == "Username")
                return $"barkcloud_{entry.ServiceId.ToString().ToLowerInvariant()}";
            if (entry.ServiceId != ServiceId.Unknown && entry.Key == "Password")
                return _rabbitPasswords.TryGetValue(entry.ServiceId, out var password) ? EmptyAsNull(password) : null;
            return null;
        }
        if (entry.Section == "Seq" && entry.Key == "ServerUrl")
            return "http://cloud-seq:5341";
        if (entry.Section == "Features" && entry.Key == "RegistrationEnabled")
            return "true";

        if (DatabaseNames.TryGetValue(entry.ServiceId, out var database) && entry.Section == database.Section)
        {
            if (!_databasePasswords.TryGetValue(entry.ServiceId, out var password) || string.IsNullOrEmpty(password))
                return null;
            return new NpgsqlConnectionStringBuilder
            {
                Host = _postgresHost,
                Port = _postgresPort,
                Database = database.Database,
                Username = database.Username,
                Password = password,
                MaxPoolSize = 20,
                ConnectionIdleLifetime = 60,
                ConnectionPruningInterval = 10
            }.ConnectionString;
        }

        if (entry.ServiceId == ServiceId.Notification && entry.Section == "Email" && entry.Key == "AllowInsecure")
            return "true";
        if (entry.ServiceId == ServiceId.Notification && entry.Section == "Email")
            return EmptyAsNull(entry.Key switch
            {
                "Host" => _emailHost,
                "Port" => _emailPort,
                "SenderEmail" => _emailSenderEmail,
                "SenderPassword" => _emailSenderPassword,
                _ => null
            });

        if (entry.Section == "ExternalEndpoint" && entry.Key == "Host")
            return ResolveExternalHost(entry.ServiceId);

        if (entry.ServiceId == ServiceId.Identity && entry.Section == "WebAuthn")
        {
            var rpId = ExtractHost(_externalIdentityHost);
            return entry.Key switch
            {
                "RpId" => rpId,
                "ServerName" => "BarkCloud",
                "Origins" => rpId is null ? null : $"https://{rpId}",
                _ => null
            };
        }

        if (entry.Section.EndsWith("Service", StringComparison.Ordinal))
        {
            var target = entry.Section switch
            {
                "IdentityService" => ServiceId.Identity,
                "UsersService" => ServiceId.Users,
                "FilesService" => ServiceId.Files,
                "TorrentService" => ServiceId.Torrent,
                _ => ServiceId.Unknown
            };
            if (target != ServiceId.Unknown)
                return entry.Key switch
                {
                    "Host" => $"http://{ContainerNames[target]}:{servicePorts[target]}",
                    // Сервисные JWT больше не генерируются и не хранятся в Configuration:
                    // их выдаёт Identity (ServiceCredentialsApi) по секрету сервиса.
                    _ => null
                };
        }

        if (entry.ServiceId == ServiceId.Files && entry.Section == "TempFiles" && entry.Key == "ExpiresAt")
            return "60";
        if (entry.ServiceId == ServiceId.Torrent && entry.Section == "Torrent")
            return entry.Key switch
            {
                "DownloadPath" => "/mnt/torrents",
                "PeerPort" => ResolvePort("TORRENT_PEER_PORT", 6881).ToString(),
                _ => null
            };

        return null;
    }

    private async Task SeedUniversalProfileAsync()
    {
        if (await _context.StorageProfiles.AnyAsync(profile => profile.Role == StorageProfileRoles.Universal))
            return;
        if (string.IsNullOrWhiteSpace(_minioHost)
            || string.IsNullOrWhiteSpace(_minioPort)
            || string.IsNullOrWhiteSpace(_minioAccessKey)
            || string.IsNullOrWhiteSpace(_minioSecretKey))
        {
            _logger.LogWarning("Universal S3 profile was not seeded because MINIO_* is incomplete.");
            return;
        }

        var profiles = new StorageProfileStorage(_context, _metrics);
        await profiles.SaveAsync(new StorageProfileInput(
            StorageProfileRoles.Universal,
            BuildMinioUrl(_minioHost, _minioPort),
            _minioAccessKey,
            _minioSecretKey,
            "cloud-universal",
            IsR2: false,
            IsLegacy: false,
            ProfileId: null,
            ConfirmLegacyMutation: false), "system", "seed");
    }

    private void ValidateRequiredExternalEndpoints()
    {
        if (!_requireExternalEndpoints)
            return;
        foreach (var (serviceId, value) in new[]
                 {
                     (ServiceId.Identity, _externalIdentityHost),
                     (ServiceId.Users, _externalUsersHost),
                     (ServiceId.Files, _externalFilesHost),
                     (ServiceId.Torrent, _externalTorrentHost)
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"ExternalEndpoint:Host для сервиса {serviceId} обязателен. Задайте EXTERNAL_{serviceId.ToString().ToUpperInvariant()}_HOST.");
        }
    }

    private void ValidateServiceCredentials()
    {
        foreach (var serviceId in DatabaseNames.Keys)
        {
            if (!_databasePasswords.TryGetValue(serviceId, out var password) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException($"{serviceId} PostgreSQL password is required in the Configuration environment.");
        }

        foreach (var serviceId in new[]
                 {
                     ServiceId.Identity, ServiceId.Users, ServiceId.Files,
                     ServiceId.Torrent, ServiceId.Notification
                 })
        {
            if (!_rabbitPasswords.TryGetValue(serviceId, out var password) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException($"{serviceId} RabbitMQ password is required in the Configuration environment.");
        }
    }

    private string? ResolveExternalHost(ServiceId serviceId)
    {
        var configured = serviceId switch
        {
            ServiceId.Identity => _externalIdentityHost,
            ServiceId.Users => _externalUsersHost,
            ServiceId.Files => _externalFilesHost,
            ServiceId.Torrent => _externalTorrentHost,
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return serviceId switch
        {
            ServiceId.Identity => "https://identity.example.com",
            ServiceId.Users => "https://users.example.com",
            ServiceId.Files => "https://files.example.com",
            ServiceId.Torrent => "https://torrent.example.com",
            _ => null
        };
    }

    private static int ResolvePort(string envName, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envName), out var value) && value > 0 ? value : fallback;

    private static string? ExtractHost(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string? EmptyAsNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? GetNonEmpty(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? EmptyAsNull(value) : null;

    private static string BuildMinioUrl(string host, string port) =>
        host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? host.TrimEnd('/')
            : $"http://{host}:{port}";

    private async Task<int> CountSettingsAsync()
    {
        var count = 0;
        foreach (var scope in SettingsScopes.All)
            count += await _context.Settings(scope).CountAsync();
        return count;
    }

    private async Task<int> CountEmptySettingsAsync()
    {
        var count = 0;
        foreach (var scope in SettingsScopes.All)
            count += await _context.Settings(scope).CountAsync(row => row.Value == string.Empty);
        return count;
    }

    private static string GenerateRandomKey(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%&*";
        var data = RandomNumberGenerator.GetBytes(length);
        var result = new StringBuilder(length);
        foreach (var value in data)
            result.Append(chars[value % chars.Length]);
        return result.ToString();
    }
}
