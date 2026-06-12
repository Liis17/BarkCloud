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
    private readonly string _emailHost;
    private readonly string _emailPort;
    private readonly string _emailSenderEmail;
    private readonly string _emailSenderPassword;
    private readonly string _externalIdentityHost;
    private readonly string _externalUsersHost;
    private readonly string _externalFilesHost;
    private readonly bool _requireExternalEndpoints;

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
    /// Маппинг ServiceId → (имя env-переменной с портом, фолбэк-значение).
    /// Порт берётся из .env (Configuration-контейнер получает его через env_file), чтобы записанные
    /// в БД RunSettings:Port и *Service:Host совпадали с портом, на котором реально слушает сервис
    /// (он берёт тот же env). Notification внешнего порта не имеет — только фолбэк.
    /// </summary>
    private static readonly Dictionary<ServiceId, (string? EnvName, int Fallback)> ServicePorts = new()
    {
        { ServiceId.Identity, ("IDENTITY_PORT", 7020) },
        { ServiceId.Users, ("USERS_PORT", 7021) },
        { ServiceId.Notification, (null, 7022) },
        { ServiceId.Files, ("FILES_PORT", 7025) },
    };

    private static int ResolveServicePort(ServiceId serviceId)
    {
        if (!ServicePorts.TryGetValue(serviceId, out var p))
            return 0;
        if (p.EnvName != null
            && int.TryParse(Environment.GetEnvironmentVariable(p.EnvName), out var v) && v > 0)
            return v;
        return p.Fallback;
    }

    private static int ResolveFilesHttp1Port()
        => int.TryParse(Environment.GetEnvironmentVariable("FILES_HTTP1PORT"), out var v) && v > 0 ? v : 7026;

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
        string emailHost,
        string emailPort,
        string emailSenderEmail,
        string emailSenderPassword,
        string externalIdentityHost,
        string externalUsersHost,
        string externalFilesHost,
        bool requireExternalEndpoints,
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
        _emailHost = emailHost;
        _emailPort = emailPort;
        _emailSenderEmail = emailSenderEmail;
        _emailSenderPassword = emailSenderPassword;
        _externalIdentityHost = externalIdentityHost;
        _externalUsersHost = externalUsersHost;
        _externalFilesHost = externalFilesHost;
        _requireExternalEndpoints = requireExternalEndpoints;
    }

    /// <summary>
    /// Сверяет таблицу с эталонным списком ожидаемых ключей (<see cref="ConfigurationSeed"/>)
    /// и добавляет только недостающие записи (по тройке Section/Key/ServiceId) с пустым Value.
    /// Выполняется при каждом старте, поэтому новые ключи (например, SMTP-поля Notification)
    /// доезжают в уже существующую БД, а дубликаты не создаются.
    /// Дальше <see cref="PopulateDefaultsAsync"/> заполнит новые записи дефолтами.
    /// </summary>
    public async Task EnsureSeedAsync()
    {
        var seedItems = ConfigurationSeed.BuildSeedItems();

        var existingKeys = (await _context.Configurations
                .Select(c => new { c.Section, c.Key, c.ServiceId })
                .ToListAsync())
            .Select(c => (c.Section, c.Key, c.ServiceId))
            .ToHashSet();

        var missing = seedItems
            .Where(item => !existingKeys.Contains((item.Section, item.Key, item.ServiceId)))
            .ToList();

        if (missing.Count == 0)
        {
            _logger.LogInformation("Все ожидаемые конфигурации уже присутствуют, seed не требуется");
            return;
        }

        await _context.Configurations.AddRangeAsync(missing);
        await _context.SaveChangesAsync();

        _metrics?.Add("configurations_seeded_total", missing.Count);
        _logger.LogInformation(
            "Добавлено недостающих записей конфигурации: {Count} (из {Total} ожидаемых)",
            missing.Count, seedItems.Count);
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
            var port = ResolveServicePort(serviceId);
            if (port > 0)
                return port.ToString();
        }

        // --- RunSettings:Http1Port (Files) ---
        if (config.Section == "RunSettings" && config.Key == "Http1Port")
        {
            if (serviceId == ServiceId.Files)
                return ResolveFilesHttp1Port().ToString();
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
                return $"Host={_postgresHost};Database={dbName};Username={_postgresUsername};Password={_postgresPassword};Maximum Pool Size=20;Connection Idle Lifetime=60;Connection Pruning Interval=10";
            }
        }

        // --- Email (SMTP для Notification) ---
        // Значения приходят из env (.env). Поля опциональны: если env пуст — оставляем
        // запись пустой (режим без почты). Заполняем только непустыми значениями.
        if (config.Section == "Email" && serviceId == ServiceId.Notification)
        {
            var value = config.Key switch
            {
                "Host" => _emailHost,
                "Port" => _emailPort,
                "SenderEmail" => _emailSenderEmail,
                "SenderPassword" => _emailSenderPassword,
                _ => null
            };
            return string.IsNullOrEmpty(value) ? null : value;
        }

        // --- ExternalEndpoint:Host (внешние адреса для клиентов) ---
        // Берётся из env (.env). Адреса обязательны: вне Development пустое значение —
        // ошибка старта (чтобы клиенты не получили нерабочий адрес из БД).
        if (config.Section == "ExternalEndpoint" && config.Key == "Host")
        {
            var host = serviceId switch
            {
                ServiceId.Identity => _externalIdentityHost,
                ServiceId.Users => _externalUsersHost,
                ServiceId.Files => _externalFilesHost,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(host))
                return host;

            if (_requireExternalEndpoints && SubdomainNames.ContainsKey(serviceId))
                throw new InvalidOperationException(
                    $"ExternalEndpoint:Host для сервиса {serviceId} обязателен, но env-переменная "
                    + $"EXTERNAL_{serviceId.ToString().ToUpperInvariant()}_HOST не задана. "
                    + "Укажите её в .env (например, https://example.com).");

            // Development: допускаем плейсхолдер, чтобы локальный запуск не падал.
            if (SubdomainNames.TryGetValue(serviceId, out var subdomain))
                return $"https://{subdomain}.example.com";
        }

        // --- UsersService (inter-service) ---
        if (config.Section == "UsersService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Users]}:{ResolveServicePort(ServiceId.Users)}",
                "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, "UsersServiceClient"),
                _ => null
            };
        }

        // --- FilesService (inter-service) ---
        if (config.Section == "FilesService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Files]}:{ResolveServicePort(ServiceId.Files)}",
                "Token" => GenerateServiceToken(jwtSecret, jwtIssuer, jwtAudience, "FilesServiceClient"),
                _ => null
            };
        }

        // --- IdentityService (inter-service) ---
        if (config.Section == "IdentityService")
        {
            return config.Key switch
            {
                "Host" => $"http://{ContainerNames[ServiceId.Identity]}:{ResolveServicePort(ServiceId.Identity)}",
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
