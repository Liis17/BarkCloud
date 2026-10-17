namespace BarkCloud.Builder;

/// <summary>
/// Параметры генерации docker-compose.yml и .env для бэкенда BarkCloud.
/// Дефолты основаны на Backend/sample.env; хост-пути данных относительны к папке compose.
/// </summary>
public sealed class BuilderModel
{
    // Сервисы, включаемые в compose. Ядро (configuration/identity/users/files)
    // и web — всегда; web отключить нельзя.
    public bool UseNginx { get; set; } = true;
    public string NginxLocation { get; set; } = "External"; // "Compose" | "External"
    public bool IncludeNginx => UseNginx && NginxLocation == "Compose";
    public bool IncludeNotification { get; set; } = true;
    public bool IncludeMinio { get; set; } = true;
    public bool IncludeRabbitmq { get; set; } = true;
    public bool IncludePostgres { get; set; } = true;
    public bool IncludeSeq { get; set; } = true;
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

    // Образы: реестр фиксирован, выбирается только канал (Release/Nightly/Dev).
    public const string ImageRegistry = "docker.barkfluff.com";
    public string ImageChannel { get; set; } = "Release"; // "Release" | "Nightly" | "Dev"

    // Общие
    public string ConfigurationAccessKey { get; set; } = "";
    public string AspNetCoreEnvironment { get; set; } = "Production";

    // MinIO
    public string MinioRootUser { get; set; } = "user";
    public string MinioRootPassword { get; set; } = "password";
    public string MinioPort { get; set; } = "9000";
    public string MinioWebPort { get; set; } = "9001";
    public string MinioDataPath { get; set; } = "./data/minio";

    // RabbitMQ
    public string RabbitUser { get; set; } = "user";
    public string RabbitPass { get; set; } = "password";

    // PostgreSQL
    public string PostgresUser { get; set; } = "user";
    public string PostgresPassword { get; set; } = "password";
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

    // Веб-клиент
    public string WebPort { get; set; } = "8080";
    public bool WebCookieSecure { get; set; } = false;
    public string WebPublicHost { get; set; } = "https://cloud.barkfluff.com";
    public string WebAdminPassword { get; set; } = "";

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
    ];
}

public sealed record BuilderContainerName(string EnvironmentVariable, string ServiceAlias, string Name, bool Enabled);
