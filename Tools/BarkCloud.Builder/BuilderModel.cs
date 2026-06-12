namespace BarkCloud.Builder;

/// <summary>
/// Параметры генерации docker-compose.yml и .env для бэкенда BarkCloud.
/// Дефолты соответствуют Backend/sample.env.
/// </summary>
public sealed class BuilderModel
{
    // Сервисы, включаемые в compose (ядро configuration/identity/users/files — всегда).
    public bool IncludeWeb { get; set; } = true;
    public bool IncludeNginx { get; set; } = true;
    public bool IncludeNotification { get; set; } = true;
    public bool IncludeMinio { get; set; } = true;
    public bool IncludeRabbitmq { get; set; } = true;
    public bool IncludePostgres { get; set; } = true;
    public bool IncludeSeq { get; set; } = true;

    // Образы
    public string ImageRegistryPrefix { get; set; } = "docker.barkfluff.com:5000/barkcloud-";
    public string ImageTag { get; set; } = "latest";

    // Общие
    public string ConfigurationServiceUrl { get; set; } = "http://cloud-configuration:7003";
    public string ConfigurationAccessKey { get; set; } = "";
    public string AspNetCoreEnvironment { get; set; } = "Production";

    // MinIO
    public string MinioRootUser { get; set; } = "user";
    public string MinioRootPassword { get; set; } = "password";
    public string MinioPort { get; set; } = "9020";
    public string MinioWebPort { get; set; } = "9021";
    public string MinioDataPath { get; set; } = "";
    public string ArchiveTempPath { get; set; } = "";

    // RabbitMQ
    public string RabbitUser { get; set; } = "user";
    public string RabbitPass { get; set; } = "password";

    // PostgreSQL
    public string PostgresUser { get; set; } = "user";
    public string PostgresPassword { get; set; } = "password";
    public string PostgresDb { get; set; } = "postgres";
    public string PostgresPort { get; set; } = "6543";
    public string PostgresDataPath { get; set; } = "";
    public string BackupPath { get; set; } = "";

    // Configuration → Postgres
    public string ConfigurationHost { get; set; } = "postgres:6543";
    public string ConfigurationDatabase { get; set; } = "configuration";
    public string ConfigurationUsername { get; set; } = "user";
    public string ConfigurationPassword { get; set; } = "password";
    public string ConfigurationPort { get; set; } = "7023";

    // Порты сервисов
    public string IdentityPort { get; set; } = "7020";
    public string UsersPort { get; set; } = "7021";
    public string FilesPort { get; set; } = "7025";
    public string FilesHttp1Port { get; set; } = "7026";

    // Seq
    public string SeqAdminPassword { get; set; } = "password";
    public string SeqWebPort { get; set; } = "8881";
    public string SeqDataPath { get; set; } = "";

    // Веб-клиент
    public string WebPort { get; set; } = "63222";
    public bool WebCookieSecure { get; set; } = false;
    public string WebPublicHost { get; set; } = "";
    public string WebAdminPassword { get; set; } = "";

    // Вывод
    public string OutputPath { get; set; } = "";
}
