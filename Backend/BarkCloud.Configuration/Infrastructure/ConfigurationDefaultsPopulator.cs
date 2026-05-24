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
/// Автоматическое заполнение пустых конфигураций значениями по умолчанию.
/// Запускается после миграций при старте Configuration-сервиса.
/// </summary>
public class ConfigurationDefaultsPopulator
{
    private readonly ConfigurationContext _context;
    private readonly ILogger<ConfigurationDefaultsPopulator> _logger;
    private readonly MetricsCollector? _metrics;
    private readonly string _postgresHost;
    private readonly string _postgresUsername;
    private readonly string _postgresPassword;
    private readonly string _rabbitUsername;
    private readonly string _rabbitPassword;
    private readonly string _minioHost;
    private readonly string _minioPort;
    private readonly string _minioAccessKey;
    private readonly string _minioSecretKey;

    /// <summary>
    /// Маппинг ServiceId → имя контейнера в Docker
    /// </summary>
    private static readonly Dictionary<ServiceId, string> ContainerNames = new()
    {
        { ServiceId.Identity, "cloud-identity" },
        { ServiceId.Users, "cloud-users" },
        { ServiceId.Files, "cloud-files" },
    };

    /// <summary>
    /// Маппинг ServiceId → порт по умолчанию
    /// </summary>
    private static readonly Dictionary<ServiceId, int> DefaultPorts = new()
    {
        { ServiceId.Identity, 7000 },
        { ServiceId.Users, 7001 },
        { ServiceId.Notification, 7022 },
        { ServiceId.Files, 7005 },
    };

    /// <summary>
    /// Маппинг ServiceId → субдомен для внешнего доступа
    /// </summary>
    private static readonly Dictionary<ServiceId, string> SubdomainNames = new()
    {
        { ServiceId.Identity, "identity" },
        { ServiceId.Users, "users" },
        { ServiceId.Files, "files" },
    };

    /// <summary>
    /// Маппинг ServiceId → имя базы данных
    /// </summary>
    private static readonly Dictionary<ServiceId, (string Section, string DbName)> DatabaseNames = new()
    {
        { ServiceId.Identity, ("IdentityDb", "identity") },
        { ServiceId.Users, ("UsersDb", "users") },
        { ServiceId.Files, ("FilesDb", "files") },
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
        MetricsCollector? metrics = null)
    {
        _context = context;
        _logger = logger;
        _metrics = metrics;
        _postgresHost = postgresHost;
        _postgresUsername = postgresUsername;
        _postgresPassword = postgresPassword;
        _rabbitUsername = rabbitUsername;
        _rabbitPassword = rabbitPassword;
        _minioHost = minioHost;
        _minioPort = minioPort;
        _minioAccessKey = minioAccessKey;
        _minioSecretKey = minioSecretKey;
    }

    /// <summary>
    /// При пустой таблице создаёт записи для всех ожидаемых ключей (Section/Key/ServiceId)
    /// с пустым Value. Дальше <see cref="PopulateDefaultsAsync"/> заполнит их дефолтами.
    /// </summary>
    public async Task EnsureSeedAsync()
    {
        var hasAny = await _context.Configurations.AnyAsync();
        if (hasAny)
        {
            _logger.LogInformation("Таблица Configurations уже содержит записи, seed не требуется");
            return;
        }

        var seedItems = ConfigurationSeed.BuildSeedItems();
        await _context.Configurations.AddRangeAsync(seedItems);
        await _context.SaveChangesAsync();

        _metrics?.Add("configurations_seeded_total", seedItems.Count);
        _logger.LogInformation("Засеяно пустых записей конфигурации: {Count}", seedItems.Count);
    }

    public async Task PopulateDefaultsAsync()
    {
        var emptyConfigs = await _context.Configurations
            .Where(c => c.Value == "" || c.Value == null)
            .ToListAsync();

        // Стартовый gauge — общее число записей
        var totalConfigs = await _context.Configurations.CountAsync();
        _metrics?.Set("configurations_total", totalConfigs);

        if (emptyConfigs.Count == 0)
        {
            _logger.LogInformation("Все конфигурации уже заполнены, авто-заполнение не требуется");
            _metrics?.Set("configurations_empty_at_startup", 0);
            return;
        }

        _logger.LogInformation("Найдено {Count} пустых конфигураций, запуск авто-заполнения", emptyConfigs.Count);
        _metrics?.Set("configurations_empty_at_startup", emptyConfigs.Count);

        // Сначала заполняем JWT SecretKey, т.к. он нужен для генерации сервисных токенов
        var jwtSecret = await GetOrGenerateJwtSecret(emptyConfigs);
        var jwtIssuer = await GetOrGenerateValue(emptyConfigs, "JwtSettings", "Issuer", "BarkCloud");
        var jwtAudience = await GetOrGenerateValue(emptyConfigs, "JwtSettings", "Audience", "BarkCloudMicroservices");

        var populatedCount = 0;
        foreach (var config in emptyConfigs)
        {
            var defaultValue = ResolveDefault(config, jwtSecret, jwtIssuer, jwtAudience);
            if (defaultValue != null)
            {
                config.Value = defaultValue;
                config.EditedAt = DateTime.UtcNow;
                config.EditedBy = "system";
                config.EditedFrom = "auto-populate";
                populatedCount++;
                _logger.LogDebug("Авто-заполнение: [{ServiceId}] {Section}:{Key} = {Value}",
                    config.ServiceId, config.Section, config.Key,
                    IsSensitive(config) ? "***" : defaultValue);
            }
        }

        await _context.SaveChangesAsync();
        _metrics?.Add("defaults_populated_total", populatedCount);
        _metrics?.Set("configurations_total", await _context.Configurations.CountAsync());
        _logger.LogInformation("Авто-заполнение завершено. Заполнено: {Count}", populatedCount);
    }

    private async Task<string> GetOrGenerateJwtSecret(List<ConfigurationItem> emptyConfigs)
    {
        var secretConfig = emptyConfigs.FirstOrDefault(
            c => c.Section == "JwtSettings" && c.Key == "SecretKey");

        if (secretConfig != null)
        {
            var secret = GenerateRandomKey(64);
            secretConfig.Value = secret;
            secretConfig.EditedAt = DateTime.UtcNow;
            secretConfig.EditedBy = "system";
            secretConfig.EditedFrom = "auto-populate";
            return secret;
        }

        // Если SecretKey уже заполнен, читаем его для генерации токенов
        var existing = await _context.Configurations
            .Where(c => c.Section == "JwtSettings" && c.Key == "SecretKey" && c.Value != "" && c.Value != null)
            .FirstOrDefaultAsync();

        return existing?.Value ?? GenerateRandomKey(64);
    }

    private async Task<string> GetOrGenerateValue(
        List<ConfigurationItem> emptyConfigs, string section, string key, string defaultValue)
    {
        var config = emptyConfigs.FirstOrDefault(c => c.Section == section && c.Key == key);
        if (config != null)
        {
            config.Value = defaultValue;
            config.EditedAt = DateTime.UtcNow;
            config.EditedBy = "system";
            config.EditedFrom = "auto-populate";
            return defaultValue;
        }

        var existing = await _context.Configurations
            .Where(c => c.Section == section && c.Key == key && c.Value != "" && c.Value != null)
            .FirstOrDefaultAsync();

        return existing?.Value ?? defaultValue;
    }

    private string? ResolveDefault(ConfigurationItem config, string jwtSecret, string jwtIssuer, string jwtAudience)
    {
        // Пропускаем уже заполненные (могли быть заполнены в GetOrGenerate)
        if (!string.IsNullOrEmpty(config.Value))
            return null;

        var serviceId = config.ServiceId;

        // --- RunSettings:Port ---
        if (config.Section == "RunSettings" && config.Key == "Port")
        {
            if (DefaultPorts.TryGetValue(serviceId, out var port))
                return port.ToString();
        }

        // --- RunSettings:Http1Port (Files) ---
        if (config.Section == "RunSettings" && config.Key == "Http1Port")
        {
            if (serviceId == ServiceId.Files)
                return "7006";
        }

        // --- JwtSettings ---
        if (config.Section == "JwtSettings")
        {
            return config.Key switch
            {
                "SecretKey" => null, // Уже обработано выше
                "Issuer" => null,    // Уже обработано выше
                "Audience" => null,  // Уже обработано выше
                "ExpiryMinutes" => "60",
                _ => null
            };
        }

        // --- RabbitMQ (внутренний Docker-адрес) ---
        if (config.Section == "RabbitMQ")
        {
            return config.Key switch
            {
                "Host" => "rabbitmq",
                "Username" => _rabbitUsername,
                "Password" => _rabbitPassword,
                "VirtualHost" => "/",
                _ => null
            };
        }

        // --- Seq (агрегатор логов) ---
        if (config.Section == "Seq" && config.Key == "ServerUrl")
        {
            return "http://seq:5341";
        }

        // --- Database connection strings ---
        foreach (var (sId, (section, dbName)) in DatabaseNames)
        {
            if (config.Section == section && config.ServiceId == sId)
            {
                return $"Host={_postgresHost};Database={dbName};Username={_postgresUsername};Password={_postgresPassword}";
            }
        }

        // --- ExternalEndpoint:Host (внешние субдомены) ---
        if (config.Section == "ExternalEndpoint" && config.Key == "Host")
        {
            if (SubdomainNames.TryGetValue(serviceId, out var subdomain))
                return $"https://{subdomain}.example.com";
        }

        // --- UsersService (inter-service) ---
        if (config.Section == "UsersService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Users]}:{DefaultPorts[ServiceId.Users]}",
                "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, "UsersServiceClient"),
                _ => null
            };
        }

        // --- FilesService (inter-service) ---
        if (config.Section == "FilesService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Files]}:{DefaultPorts[ServiceId.Files]}",
                "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, "FilesServiceClient"),
                _ => null
            };
        }

        // --- IdentityService (inter-service) ---
        if (config.Section == "IdentityService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Identity]}:{DefaultPorts[ServiceId.Identity]}",
                "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, "IdentityServiceClient"),
                _ => null
            };
        }

        // --- TempFiles ---
        if (config.Section == "TempFiles" && config.Key == "ExpiresAt")
        {
            return "60"; // минуты
        }

        // --- S3 Buckets (внутренний Docker-адрес MinIO) ---
        if (config.Section.StartsWith("S3Buckets:"))
        {
            // Имя бакета извлекаем из секции вида "S3Buckets:<bucket-id>"
            var bucketId = config.Section.Substring("S3Buckets:".Length);

            return config.Key switch
            {
                "ServiceUrl"     => $"http://{_minioHost}:{_minioPort}",
                "AccessKey"      => _minioAccessKey,
                "SecretKey"      => _minioSecretKey,
                "BucketName"     => bucketId,
                "ForcePathStyle" => "true",
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Генерация JWT-токена для межсервисного взаимодействия (TokenType = Service)
    /// </summary>
    private static string GenerateServiceToken(string secretKey, string issuer, string audience, string serviceName)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(IdentityClaims.TokenType, nameof(TokenType.Service)),
            new Claim(IdentityClaims.UserId, "0"),
            new Claim("service-name", serviceName),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddYears(10),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Генерация криптографически стойкого случайного ключа
    /// </summary>
    private static string GenerateRandomKey(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%&*";
        var data = RandomNumberGenerator.GetBytes(length);
        var result = new StringBuilder(length);
        foreach (var b in data)
        {
            result.Append(chars[b % chars.Length]);
        }
        return result.ToString();
    }

    private static bool IsSensitive(ConfigurationItem config)
    {
        return config.Key is "SecretKey" or "Password" or "Token"
               || config.Section.Contains("Password")
               || config.Section.Contains("Secret")
               || config.Section.Contains("Token");
    }
}
