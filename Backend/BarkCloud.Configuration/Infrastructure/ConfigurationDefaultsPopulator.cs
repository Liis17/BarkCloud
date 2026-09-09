using BarkCloud.Configuration.Catalog;
using BarkCloud.Configuration.Domain;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Configuration.Infrastructure;

/// <summary>
/// Idempotently creates catalog rows and fills only empty values from environment-backed defaults.
/// </summary>
public class ConfigurationDefaultsPopulator
{
    private readonly ConfigurationContext _context;
    private readonly ILogger<ConfigurationDefaultsPopulator> _logger;
    private readonly MetricsCollector _metrics;
    private readonly string _postgresHost;
    private readonly string _postgresUsername;
    private readonly string _postgresPassword;
    private readonly string _rabbitUsername;
    private readonly string _rabbitPassword;
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

    private static readonly IReadOnlyDictionary<ServiceId, (string Section, string Database)> DatabaseNames =
        new Dictionary<ServiceId, (string, string)>
        {
            [ServiceId.Identity] = ("IdentityDb", "identity"),
            [ServiceId.Users] = ("UsersDb", "users"),
            [ServiceId.Files] = ("FilesDb", "files"),
            [ServiceId.Torrent] = ("TorrentDb", "torrent")
        };

    public ConfigurationDefaultsPopulator(
        ConfigurationContext context,
        ILogger<ConfigurationDefaultsPopulator> logger,
        string postgresHost,
        string postgresUsername,
        string postgresPassword,
        string rabbitUsername,
        string rabbitPassword,
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
        _postgresUsername = postgresUsername;
        _postgresPassword = postgresPassword;
        _rabbitUsername = rabbitUsername;
        _rabbitPassword = rabbitPassword;
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
        var global = SettingsScopes.Get(ServiceId.Unknown);
        var globalRows = await _context.Settings(global).AsNoTracking()
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var jwtSecret = GetNonEmpty(globalRows, "JwtSettings:SecretKey") ?? GenerateRandomKey(64);
        var jwtIssuer = GetNonEmpty(globalRows, "JwtSettings:Issuer") ?? "BarkCloud";
        var jwtAudience = GetNonEmpty(globalRows, "JwtSettings:Audience") ?? "BarkCloudMicroservices";

        ValidateRequiredExternalEndpoints();
        var beforeEmpty = await CountEmptySettingsAsync();
        var storage = new ConfigurationStorage(_context, _metrics);
        await storage.SeedMissingCatalogRowsAsync(entry =>
            ResolveDefault(entry, jwtSecret, jwtIssuer, jwtAudience));
        var afterEmpty = await CountEmptySettingsAsync();
        var populated = beforeEmpty - afterEmpty;
        _metrics.Add("defaults_populated_total", populated);
        _metrics.Set("configurations_empty_at_startup", afterEmpty);

        await SeedUniversalProfileAsync();
        _logger.LogInformation("Авто-заполнение завершено. Заполнено: {Count}", populated);
    }

    private string? ResolveDefault(
        SettingsCatalogEntry entry,
        string jwtSecret,
        string jwtIssuer,
        string jwtAudience)
    {
        if (entry.Section == "RunSettings" && entry.Key == "Port")
            return ResolveServicePort(entry.ServiceId).ToString();
        if (entry.Section == "RunSettings" && entry.Key == "Http1Port")
            return entry.ServiceId == ServiceId.Files ? ResolvePort("FILES_HTTP1PORT", 7026).ToString()
                : ResolvePort("TORRENT_HTTP1PORT", 7028).ToString();

        if (entry.Section == "JwtSettings")
            return entry.Key switch
            {
                "SecretKey" => jwtSecret,
                "Issuer" => jwtIssuer,
                "Audience" => jwtAudience,
                "ExpiryMinutes" => "60",
                _ => null
            };

        if (entry.Section == "RabbitMQ")
            return entry.Key switch
            {
                "Host" => "cloud-rabbitmq",
                "Username" => EmptyAsNull(_rabbitUsername),
                "Password" => EmptyAsNull(_rabbitPassword),
                "VirtualHost" => "/",
                _ => null
            };
        if (entry.Section == "Seq" && entry.Key == "ServerUrl")
            return "http://cloud-seq:5341";
        if (entry.Section == "Features" && entry.Key == "RegistrationEnabled")
            return "true";

        if (DatabaseNames.TryGetValue(entry.ServiceId, out var database) && entry.Section == database.Section)
            return $"Host={_postgresHost};Database={database.Database};Username={_postgresUsername};Password={_postgresPassword};Maximum Pool Size=20;Connection Idle Lifetime=60;Connection Pruning Interval=10";

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
                    "Host" => $"http://{ContainerNames[target]}:{ResolveServicePort(target)}",
                    "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, $"{entry.Section}Client"),
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

    private static int ResolveServicePort(ServiceId serviceId)
    {
        var setting = ServicePorts[serviceId];
        return setting.EnvName is null ? setting.Fallback : ResolvePort(setting.EnvName, setting.Fallback);
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

    private static string GenerateServiceToken(
        string secretKey,
        string issuer,
        string audience,
        string serviceName)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secretKey)),
            SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(IdentityClaims.TokenType, nameof(TokenType.Service)),
            new Claim(IdentityClaims.UserId, "0"),
            new Claim("service-name", serviceName)
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: DateTime.UtcNow.AddYears(10),
            signingCredentials: credentials));
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
