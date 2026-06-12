namespace BarkCloud.Builder;

/// <summary>
/// Параметры генерации docker-compose.yml и .env для бэкенда BarkCloud.
/// Дефолты соответствуют Backend/sample.env.
/// </summary>
public sealed class BuilderModel
{
    // Сервисы, включаемые в compose. Ядро (configuration/identity/users/files)
    // и web — всегда; web отключить нельзя.
    public bool IncludeNginx { get; set; } = true;
    public bool IncludeNotification { get; set; } = true;
    public bool IncludeMinio { get; set; } = true;
    public bool IncludeRabbitmq { get; set; } = true;
    public bool IncludePostgres { get; set; } = true;
    public bool IncludeSeq { get; set; } = true;

    // Образы: реестр фиксирован, выбирается только канал (Release/Dev).
    public const string ImageRegistry = "docker.barkfluff.com:5000";
    public string ImageChannel { get; set; } = "Release"; // "Release" | "Dev"

    // Общие
    public string ConfigurationAccessKey { get; set; } = "";
    public string AspNetCoreEnvironment { get; set; } = "Production";

    // MinIO
    public string MinioRootUser { get; set; } = "user";
    public string MinioRootPassword { get; set; } = "password";
    public string MinioPort { get; set; } = "9020";
    public string MinioWebPort { get; set; } = "9021";
    public string MinioDataPath { get; set; } = "/d/barkcloud/minio";

    // RabbitMQ
    public string RabbitUser { get; set; } = "user";
    public string RabbitPass { get; set; } = "password";

    // PostgreSQL
    public string PostgresUser { get; set; } = "user";
    public string PostgresPassword { get; set; } = "password";
    public string PostgresDb { get; set; } = "postgrescloud";
    public string PostgresPort { get; set; } = "6543";
    public string PostgresDataPath { get; set; } = "/d/barkcloud/pgdata";
    public string BackupPath { get; set; } = "/d/barkcloud/backup";

    // Порты сервисов
    public string IdentityPort { get; set; } = "7020";
    public string UsersPort { get; set; } = "7021";
    public string ConfigurationPort { get; set; } = "7023";
    public string FilesPort { get; set; } = "7025";
    public string FilesHttp1Port { get; set; } = "7026";

    // Seq
    public string SeqAdminPassword { get; set; } = "password";
    public string SeqWebPort { get; set; } = "8881";
    public string SeqDataPath { get; set; } = "/d/barkcloud/seq";

    // Веб-клиент
    public string WebPort { get; set; } = "63222";
    public bool WebCookieSecure { get; set; } = false;
    public string WebPublicHost { get; set; } = "https://cloud.barkfluff.com";
    public string WebAdminPassword { get; set; } = "";

    // Nginx / HTTPS (server_name + сертификаты). Пути — к исходным файлам на диске;
    // в конфиг и в папку certs попадают их имена (basename).
    public string NginxDomain { get; set; } = "cloud.barkfluff.com";
    public string CertCrtPath { get; set; } = "";
    public string CertKeyPath { get; set; } = "";

    // Вывод
    public string OutputPath { get; set; } = "";
}
