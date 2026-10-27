using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BarkCloud.Builder;

/// <summary>
/// Параметры генерации docker-compose.yml и .env для бэкенда BarkCloud.
/// Дефолты основаны на Backend/sample.env; хост-пути данных относительны к папке compose.
/// </summary>
public sealed class BuilderModel : INotifyPropertyChanged
{
    private bool _includeSeq;
    private bool _includeLognara;

    // Сервисы, включаемые в compose. Ядро (configuration/identity/users/files)
    // и web — всегда; web отключить нельзя.
    public bool UseNginx { get; set; } = true;
    public string NginxLocation { get; set; } = "External"; // "Compose" | "External"
    public bool IncludeNginx => UseNginx && NginxLocation == "Compose";
    public bool IncludeNotification { get; set; } = true;
    public bool IncludeMinio { get; set; } = true;
    public bool IncludeRabbitmq { get; set; } = true;
    public bool IncludePostgres { get; set; } = true;
    public bool IncludeSeq { get => _includeSeq; set => Set(ref _includeSeq, value); }
    public bool IncludeTorrent { get; set; } = true;

    // Имена контейнеров уникальны в Docker daemon. DNS-имена Compose-сервисов
    // cloud-* остаются отдельным стабильным контрактом.
    public string DockerNetworkName { get; set; } = "barkcloud-network";
    public string ConfigurationContainerName { get; set; } = "cloud-configuration";
    public string IdentityContainerName { get; set; } = "cloud-identity";
    public string UsersContainerName { get; set; } = "cloud-users";
    public string FilesContainerName { get; set; } = "cloud-files";
    public string NotificationContainerName { get; set; } = "cloud-notification";
    public string TorrentContainerName { get; set; } = "cloud-torrent";
    public string WebContainerName { get; set; } = "cloud-web";
    public string MinioContainerName { get; set; } = "cloud-minio";
    public string RabbitmqContainerName { get; set; } = "cloud-rabbitmq";
    public string PostgresContainerName { get; set; } = "cloud-postgres";
    public string SeqContainerName { get; set; } = "cloud-seq";
    public string NginxContainerName { get; set; } = "cloud-nginx";

    // Образы: реестр фиксирован, выбирается только канал (Release/Dev).
    public const string ImageRegistry = "docker.barkfluff.com";
    public string ImageChannel { get; set; } = "Release"; // "Release" | "Dev"

    // Общие
    public string AspNetCoreEnvironment { get; set; } = "Production";

    // Разделённые секреты (аудит, п. 9): bootstrap-ключи Configuration и секреты сервисных
    // токенов у каждого сервиса свои; реестры собираются из них в BuildEnv.
    public string IdentityConfigAccessKey { get; set; } = "";
    public string UsersConfigAccessKey { get; set; } = "";
    public string NotificationConfigAccessKey { get; set; } = "";
    public string FilesConfigAccessKey { get; set; } = "";
    public string TorrentConfigAccessKey { get; set; } = "";
    public string WebConfigAccessKey { get; set; } = "";
    public string IdentityServiceCredential { get; set; } = "";
    public string UsersServiceCredential { get; set; } = "";
    public string FilesServiceCredential { get; set; } = "";
    public string TorrentServiceCredential { get; set; } = "";
    public string WebServiceCredential { get; set; } = "";

    // Deployment-only credentials are generated internally; they are not exposed as UI fields.
    public string ConfigurationDbPassword { get; set; } = GenerateSecret(48);
    public string IdentityDbPassword { get; set; } = GenerateSecret(48);
    public string UsersDbPassword { get; set; } = GenerateSecret(48);
    public string FilesDbPassword { get; set; } = GenerateSecret(48);
    public string TorrentDbPassword { get; set; } = GenerateSecret(48);
    public string IdentityRabbitPassword { get; set; } = GenerateSecret(48);
    public string UsersRabbitPassword { get; set; } = GenerateSecret(48);
    public string FilesRabbitPassword { get; set; } = GenerateSecret(48);
    public string TorrentRabbitPassword { get; set; } = GenerateSecret(48);
    public string NotificationRabbitPassword { get; set; } = GenerateSecret(48);

    public string ConfigurationAccessKeysRegistry =>
        $"Identity={IdentityConfigAccessKey};Users={UsersConfigAccessKey};Notification={NotificationConfigAccessKey};Files={FilesConfigAccessKey};Torrent={TorrentConfigAccessKey};Web={WebConfigAccessKey}";

    public string ServiceCredentialsRegistry =>
        $"Identity={IdentityServiceCredential};Users={UsersServiceCredential};Files={FilesServiceCredential};Torrent={TorrentServiceCredential};Web={WebServiceCredential}";

    // MinIO
    public string MinioRootUser { get; set; } = "user";
    public string MinioRootPassword { get; set; } = "password";
    public string MinioPort { get; set; } = "9000";
    public string MinioWebPort { get; set; } = "9001";
    public string MinioDataPath { get; set; } = "./data/minio";

    // RabbitMQ
    public string RabbitUser { get; set; } = "user";
    public string RabbitPass { get; set; } = GenerateSecret(48);
    public string RabbitHost { get; set; } = "cloud-rabbitmq";
    public string RabbitVirtualHost { get; set; } = "/";

    // PostgreSQL
    public string PostgresUser { get; set; } = "user";
    public string PostgresPassword { get; set; } = GenerateSecret(48);
    public string PostgresHost { get; set; } = "cloud-postgres";
    public string PostgresDb { get; set; } = "postgrescloud";
    public string PostgresPort { get; set; } = "5432";
    public string PostgresDataPath { get; set; } = "./data/pgdata";
    public string BackupPath { get; set; } = "./data/pgbackup";

    // Порты сервисов
    public string IdentityPort { get; set; } = "8000";
    public string UsersPort { get; set; } = "8001";
    public string ConfigurationPort { get; set; } = "8003";
    public string FilesPort { get; set; } = "8005";
    public string FilesHttp1Port { get; set; } = "8006";
    public string TorrentPort { get; set; } = "8007";
    public string TorrentHttp1Port { get; set; } = "8008";
    public string TorrentPeerPort { get; set; } = "6881";

    // Torrent: папка на хосте, куда качаются торренты (монтируется в /mnt/torrents).
    public string TorrentDownloadPath { get; set; } = "./data/torrent";

    // Files: внешняя папка для временных ZIP-архивов (скачивание папок/альбомов).
    // Пусто — named volume archive_temp (создаётся от root → files под uid 1654 не запишет).
    public string ArchiveTempPath { get; set; } = "./data/archive-temp";

    // Seq
    public string SeqAdminPassword { get; set; } = "password";
    public string SeqWebPort { get; set; } = "80";
    public string SeqDataPath { get; set; } = "./data/seq";

    // Lognara: локальный relay и настройки агентов. Старый внешний relay URL намеренно
    // не хранится как Core URL при импорте legacy-конфигурации.
    public bool IncludeLognara { get => _includeLognara; set => Set(ref _includeLognara, value); }
    public string LognaraCoreUrl { get; set; } = "";
    public string LognaraCoreToken { get; set; } = "";
    public string LognaraServer { get; set; } = "";
    public string LognaraBackend { get; set; } = "barkcloud";
    public string LognaraEnvironment { get; set; } = "";
    public string LognaraAgentVersion { get; set; } = "latest";
    public bool LegacyLognaraImport { get; set; }
    public bool LegacySeqConfigurationUnknown { get; set; }

    // Веб-клиент
    public string WebPort { get; set; } = "8080";
    public bool WebCookieSecure { get; set; }
    public string WebPublicHost { get; set; } = "https://cloud.barkfluff.com";
    public string WebTrustedProxyNetworks { get; set; } = "";
    public string WebAdminPassword { get; set; } = "";
    public string WebAdminCookieKey { get; set; } = "";
    public string AndroidCertificateSha256Fingerprint { get; set; } = "27:85:31:F0:57:D2:28:2F:E0:47:1D:05:68:46:6B:91:2F:9C:31:16:E7:19:F4:27:20:40:BD:FF:63:34:E8:26";

    // Внешние адреса сервисов для клиентов (обязательны). Configuration пишет их в БД
    // на чистом старте. По умолчанию — единый домен + порт сервиса (как маршрутизирует nginx).
    public string ExternalIdentityHost { get; set; } = "https://cloud.barkfluff.com:8000";
    public string ExternalUsersHost { get; set; } = "https://cloud.barkfluff.com:8001";
    public string ExternalFilesHost { get; set; } = "https://cloud.barkfluff.com:8005";
    public string ExternalTorrentHost { get; set; } = "https://cloud.barkfluff.com:8007";

    // Почта (SMTP) — опционально. Все 4 поля непусты → подтверждение по email включено; иначе выключено.
    public string EmailHost { get; set; } = "";
    public string EmailPort { get; set; } = "";
    public string EmailSenderEmail { get; set; } = "";
    public string EmailSenderPassword { get; set; } = "";

    // Nginx / HTTPS. Локальные пути в Compose-контейнер копируются в ./certs;
    // внешние пути уже должны существовать внутри внешнего nginx-контейнера.
    public string NginxDomain { get; set; } = "cloud.barkfluff.com";
    public string CertCrtPath { get; set; } = "";
    public string CertKeyPath { get; set; } = "";
    public string ExternalNginxCertificatePath { get; set; } = "/etc/nginx/certs/barkfluff.com-crt.pem";
    public string ExternalNginxKeyPath { get; set; } = "/etc/nginx/certs/barkfluff.com-key.pem";
    public bool NginxSeqProxy { get; set; }
    public string NginxSeqPort { get; set; } = "38881";
    public bool NginxMinioConsoleProxy { get; set; }
    public string NginxMinioConsolePort { get; set; } = "39021";

    // Вывод
    public string OutputPath { get; set; } = "";

    public ObservableCollection<LognaraApplicationSettings> LognaraApplications { get; } =
    [
        new("CONFIGURATION", "Configuration"),
        new("IDENTITY", "Identity"),
        new("USERS", "Users"),
        new("FILES", "Files"),
        new("NOTIFICATION", "Notification"),
        new("TORRENT", "Torrent"),
        new("WEB", "Web"),
    ];

    public ObservableCollection<LognaraInfrastructureSettings> LognaraInfrastructure { get; } =
    [
        new("NGINX", "nginx"),
        new("MINIO", "MinIO"),
        new("POSTGRES", "PostgreSQL"),
        new("RABBITMQ", "RabbitMQ"),
        new("SEQ", "Seq"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    // Выключение backend-флага сбрасывает только его канал; остальные значения независимы.
    public void SetGlobalSeq(bool enabled)
    {
        IncludeSeq = enabled;
        foreach (var application in LognaraApplications)
            application.SeqEnable = enabled;
    }

    public void SetGlobalLognara(bool enabled)
    {
        IncludeLognara = enabled;
        foreach (var application in LognaraApplications)
            application.LognaraEnable = enabled;
    }

    public bool IsApplicationIncluded(LognaraApplicationSettings application) => application.Prefix switch
    {
        "NOTIFICATION" => IncludeNotification,
        "TORRENT" => IncludeTorrent,
        _ => true,
    };

    public bool IsInfrastructureIncluded(LognaraInfrastructureSettings service) => service.Prefix switch
    {
        "NGINX" => IncludeNginx,
        "MINIO" => IncludeMinio,
        "POSTGRES" => IncludePostgres,
        "RABBITMQ" => IncludeRabbitmq,
        "SEQ" => IncludeSeq,
        _ => false,
    };

    public IReadOnlyList<BuilderContainerName> ContainerNames =>
    [
        new("CONFIGURATION_CONTAINER_NAME", "cloud-configuration", ConfigurationContainerName, true),
        new("IDENTITY_CONTAINER_NAME", "cloud-identity", IdentityContainerName, true),
        new("USERS_CONTAINER_NAME", "cloud-users", UsersContainerName, true),
        new("FILES_CONTAINER_NAME", "cloud-files", FilesContainerName, true),
        new("NOTIFICATION_CONTAINER_NAME", "cloud-notification", NotificationContainerName, IncludeNotification),
        new("TORRENT_CONTAINER_NAME", "cloud-torrent", TorrentContainerName, IncludeTorrent),
        new("WEB_CONTAINER_NAME", "cloud-web", WebContainerName, true),
        new("MINIO_CONTAINER_NAME", "cloud-minio", MinioContainerName, IncludeMinio),
        new("RABBITMQ_CONTAINER_NAME", "cloud-rabbitmq", RabbitmqContainerName, IncludeRabbitmq),
        new("POSTGRES_CONTAINER_NAME", "cloud-postgres", PostgresContainerName, IncludePostgres),
        new("SEQ_CONTAINER_NAME", "cloud-seq", SeqContainerName, IncludeSeq),
        new("NGINX_CONTAINER_NAME", "cloud-nginx", NginxContainerName, IncludeNginx),
        new("", "cloud-relay", "cloud-relay", IncludeLognara),
    ];

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Случайный секрет из букв/цифр (для bootstrap-ключей и секретов сервисов).</summary>
    public static string GenerateSecret(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (var i = 0; i < length; i++)
            result[i] = chars[data[i] % chars.Length];
        return new string(result);
    }
}

public sealed class LognaraApplicationSettings(string prefix, string displayName) : INotifyPropertyChanged
{
    private bool _consoleOutput = true;
    private bool _seqEnable;
    private bool _lognaraEnable;
    private string _token = "";
    private string _spoolPath = "";

    public string Prefix { get; } = prefix;
    public string DisplayName { get; } = displayName;
    public string TokenVariable => $"LOGNARA_{Prefix}_TOKEN";
    public string SpoolVariable => $"LOGNARA_{Prefix}_SPOOL_PATH";

    public bool ConsoleOutput { get => _consoleOutput; set => Set(ref _consoleOutput, value); }
    public bool SeqEnable { get => _seqEnable; set => Set(ref _seqEnable, value); }
    public bool LognaraEnable { get => _lognaraEnable; set => Set(ref _lognaraEnable, value); }
    public string Token { get => _token; set => Set(ref _token, value); }
    public string SpoolPath { get => _spoolPath; set => Set(ref _spoolPath, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class LognaraInfrastructureSettings(string prefix, string displayName)
{
    public string Prefix { get; } = prefix;
    public string DisplayName { get; } = displayName;
    public string TokenVariable => $"LOGNARA_{Prefix}_TOKEN";
    public string SpoolVariable => $"LOGNARA_{Prefix}_SPOOL_PATH";
    public string Token { get; set; } = "";
    public string SpoolPath { get; set; } = "";
}

public sealed record BuilderContainerName(string EnvironmentVariable, string ServiceAlias, string Name, bool Enabled);

internal static class LognaraAgentToken
{
    public static string Normalize(string value)
    {
        const string prefix = "LOGNARA_AGENT_TOKEN=";
        var token = value.Trim();
        return token.StartsWith(prefix, StringComparison.Ordinal) ? token[prefix.Length..].Trim() : token;
    }
}
