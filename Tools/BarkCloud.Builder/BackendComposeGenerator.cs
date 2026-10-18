using System.IO;
using System.Text;

namespace BarkCloud.Builder;

/// <summary>
/// Собирает docker-compose.yml (по набору включённых сервисов) и .env (по значениям параметров).
/// Сам compose почти не зависит от значений — всё подставляется из .env через ${VAR}.
/// </summary>
public static class BackendComposeGenerator
{
    public static string BuildCompose(BuilderModel m)
    {
        // Образ приложения: docker.barkfluff.com/barkcloud-<svc>[-nightly|-dev]:latest
        string suffix = m.ImageChannel switch
        {
            "Nightly" => "-nightly",
            "Dev" => "-dev",
            _ => "",
        };
        string Img(string name) => $"{BuilderModel.ImageRegistry}/barkcloud-{name}{suffix}:latest";
        string AppEnv(string prefix) => ApplicationLoggingEnvironment(m, prefix);
        string AppControls(string prefix) => ApplicationAgentControls(m, prefix);
        string AppVolume(string prefix) => ApplicationAgentVolume(m, prefix);
        string AppVolumeSection(string prefix) => ApplicationAgentVolumeSection(m, prefix);
        string InfraImage(string prefix, string original) => InfrastructureImage(m, prefix, original);
        string InfraBuild(string prefix) => InfrastructureBuild(m, prefix);
        string InfraControls(string prefix) => InfrastructureAgentControls(m, prefix);
        string InfraEnv(string prefix) => InfrastructureAgentEnvironment(m, prefix);
        string InfraEnvSection(string prefix)
        {
            var environment = InfraEnv(prefix);
            return environment.Length == 0 ? "" : "\n    environment:" + environment;
        }
        string InfraVolume(string prefix) => InfrastructureAgentVolume(m, prefix);

        // Compose service keys remain the stable DNS and depends_on contract.

        // SMTP-переменные в configuration имеют смысл только с notification (он шлёт письма).
        // Без notification — не выводим блок, чтобы не тащить пустые EMAIL_* в configuration.
        string emailEnv = m.IncludeNotification
            ? "\n      # SMTP для подтверждений по email (опционально; пусто — режим без почты)"
              + "\n      EMAIL_HOST: ${EMAIL_HOST}"
              + "\n      EMAIL_PORT: ${EMAIL_PORT}"
              + "\n      EMAIL_SENDER_EMAIL: ${EMAIL_SENDER_EMAIL}"
              + "\n      EMAIL_SENDER_PASSWORD: ${EMAIL_SENDER_PASSWORD}"
            : "";
        string torrentDependency = m.IncludeTorrent ? "\n      - cloud-torrent" : "";
        string webOptionalContainerNames = string.Concat(
            m.IncludeNotification ? "\n      Docker__ContainerNames__Notification: \"${NOTIFICATION_CONTAINER_NAME}\"" : "",
            m.IncludeTorrent ? "\n      Docker__ContainerNames__Torrent: \"${TORRENT_CONTAINER_NAME}\"" : "");
        string configurationAgentSettings = AppEnv("CONFIGURATION");
        string identityAgentSettings = AppEnv("IDENTITY");
        string usersAgentSettings = AppEnv("USERS");
        string filesAgentSettings = AppEnv("FILES");
        string webAgentSettings = AppEnv("WEB");
        string postgresLogConfig = m.IncludeLognara
            ? "\n      - \"-c\"\n      - \"logging_collector=off\"\n      - \"-c\"\n      - \"log_destination=stderr\""
            : "";

        // Каждый блок-секция не содержит завершающего перевода строки; секции склеиваются
        // через пустую строку, что даёт ровно один разделитель между сервисами.
        var sections = new List<string>
        {
            // Шапка + ядро микросервисов (всегда присутствует).
            $$"""
x-common-variables: &common-variables
  CONFIGURATION_SERVICE_URL: "${CONFIGURATION_SERVICE_URL}"
  ASPNETCORE_ENVIRONMENT: "${ASPNETCORE_ENVIRONMENT}"
  CONFIGURATION_ACCESS_KEY: "${CONFIGURATION_ACCESS_KEY}"

services:
  # === Основные микросервисы (prod-образы) ===

  cloud-configuration:
    image: {{Img("configuration")}}
    container_name: ${CONFIGURATION_CONTAINER_NAME}
    restart: always{{AppControls("CONFIGURATION")}}
    env_file:
      - .env
    environment:
      <<: *common-variables
      CONFIGURATION_HOST:  cloud-postgres:5432
      CONFIGURATION_DATABASE: configuration
      CONFIGURATION_USERNAME: ${POSTGRES_USER}
      CONFIGURATION_PASSWORD: ${POSTGRES_PASSWORD}
      CONFIGURATION_PORT: ${CONFIGURATION_PORT}
      CONFIGURATION_DBPORT: "5432"
      MINIO_HOST: cloud-minio
      MINIO_PORT: "9000"
      MINIO_ROOT_USER: ${MINIO_ROOT_USER}
      MINIO_ROOT_PASSWORD: ${MINIO_ROOT_PASSWORD}
      RABBITMQ_DEFAULT_USER: ${RABBITMQ_DEFAULT_USER}
      RABBITMQ_DEFAULT_PASS: ${RABBITMQ_DEFAULT_PASS}{{emailEnv}}
      # Внешние адреса сервисов для клиентов (обязательны)
      EXTERNAL_IDENTITY_HOST: ${EXTERNAL_IDENTITY_HOST}
      EXTERNAL_USERS_HOST: ${EXTERNAL_USERS_HOST}
      EXTERNAL_FILES_HOST: ${EXTERNAL_FILES_HOST}
      EXTERNAL_TORRENT_HOST: ${EXTERNAL_TORRENT_HOST}
      # Порты торрент-сервиса нужны configuration для вычисления TorrentService:Host/RunSettings
      TORRENT_PORT: ${TORRENT_PORT}
      TORRENT_HTTP1PORT: ${TORRENT_HTTP1PORT}
      TORRENT_PEER_PORT: ${TORRENT_PEER_PORT}{{configurationAgentSettings}}{{AppVolumeSection("CONFIGURATION")}}
    networks:
      - barkcloud-network

  cloud-identity:
    image: {{Img("identity")}}
    container_name: ${IDENTITY_CONTAINER_NAME}
    restart: always{{AppControls("IDENTITY")}}
    environment:
      <<: *common-variables
      SERVICE_PORT: ${IDENTITY_PORT}{{identityAgentSettings}}{{AppVolumeSection("IDENTITY")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration

  cloud-users:
    image: {{Img("users")}}
    container_name: ${USERS_CONTAINER_NAME}
    restart: always{{AppControls("USERS")}}
    environment:
      <<: *common-variables
      SERVICE_PORT: ${USERS_PORT}{{usersAgentSettings}}{{AppVolumeSection("USERS")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
      - cloud-identity

  cloud-files:
    image: {{Img("files")}}
    container_name: ${FILES_CONTAINER_NAME}
    restart: always{{AppControls("FILES")}}
    environment:
      <<: *common-variables
      SERVICE_PORT: ${FILES_PORT}
      SERVICE_HTTP1PORT: ${FILES_HTTP1PORT}
      StorageProbe__Path: "/mnt/minio-data"
      Archive__TempPath: "/mnt/archive-temp"{{filesAgentSettings}}
    volumes:
      - ${MINIO_DATA_PATH:-cloud-minio_data}:/mnt/minio-data:ro
      - ${ARCHIVE_TEMP_PATH:-archive_temp}:/mnt/archive-temp{{AppVolume("FILES")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
      - cloud-identity
""",
        };

        if (m.IncludeNotification)
            sections.Add($$"""
  # Сервис уведомлений. Внешнего API нет — в nginx не маршрутизируется. Опционален
  cloud-notification:
    image: {{Img("notification")}}
    container_name: ${NOTIFICATION_CONTAINER_NAME}
    restart: always{{AppControls("NOTIFICATION")}}
    environment:
      <<: *common-variables{{AppEnv("NOTIFICATION")}}{{AppVolumeSection("NOTIFICATION")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
""");

        if (m.IncludeTorrent)
        {
            sections.Add($$"""
  # Торрент-сервис: качает торренты на хост-диск ({TORRENT_DOWNLOAD_PATH} → /mnt/torrents)
  cloud-torrent:
    image: {{Img("torrent")}}
    container_name: ${TORRENT_CONTAINER_NAME}
    restart: always{{AppControls("TORRENT")}}
    environment:
      <<: *common-variables
      SERVICE_PORT: ${TORRENT_PORT}
      SERVICE_HTTP1PORT: ${TORRENT_HTTP1PORT}
      FILES_HTTP1PORT: ${FILES_HTTP1PORT}
      Torrent__DownloadPath: "/mnt/torrents"
      Torrent__PeerPort: ${TORRENT_PEER_PORT}{{AppEnv("TORRENT")}}
    volumes:
      - ${TORRENT_DOWNLOAD_PATH:-torrent_data}:/mnt/torrents{{AppVolume("TORRENT")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
      - cloud-identity
""");
        }

        // Веб-клиент — всегда (отключить нельзя).
        sections.Add($$"""
  # Веб-клиент
  cloud-web:
    image: {{Img("web")}}
    container_name: ${WEB_CONTAINER_NAME}
    restart: always{{AppControls("WEB")}}
    user: root
    env_file:
      - .env
    environment:
      <<: *common-variables
      ASPNETCORE_URLS: "http://+:8080"
      App__CookieSecure: "${WEB_COOKIE_SECURE}"
      App__PublicHost: "${WEB_PUBLIC_HOST}"
      App__AdminPassword: "${WEB_ADMIN_PASSWORD}"
      Docker__ContainerNames__Configuration: "${CONFIGURATION_CONTAINER_NAME}"
      Docker__ContainerNames__Identity: "${IDENTITY_CONTAINER_NAME}"
      Docker__ContainerNames__Users: "${USERS_CONTAINER_NAME}"
      Docker__ContainerNames__Files: "${FILES_CONTAINER_NAME}"
      Docker__ContainerNames__Web: "${WEB_CONTAINER_NAME}"{{webOptionalContainerNames}}{{webAgentSettings}}
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./docker-compose.yml:/docker-compose.yml:rw
      - ./.env:/.env:ro
      - cloud-web-maintenance:/app/maintenance{{AppVolume("WEB")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
      - cloud-identity
      - cloud-users
      - cloud-files{{torrentDependency}}
""");

        if (m.IncludeNginx)
        {
            var nginxPorts = new List<string>
            {
                "      - \"443:443\"",
                "      - \"${IDENTITY_PORT}:${IDENTITY_PORT}\"",
                "      - \"${USERS_PORT}:${USERS_PORT}\"",
                "      - \"${FILES_PORT}:${FILES_PORT}\"",
            };
            if (m.IncludeTorrent)
                nginxPorts.Add("      - \"${TORRENT_PORT}:${TORRENT_PORT}\"");
            if (m.NginxSeqProxy && m.IncludeSeq)
                nginxPorts.Add("      - \"${NGINX_SEQ_PORT}:${NGINX_SEQ_PORT}\"");
            if (m.NginxMinioConsoleProxy && m.IncludeMinio)
                nginxPorts.Add("      - \"${NGINX_MINIO_CONSOLE_PORT}:${NGINX_MINIO_CONSOLE_PORT}\"");

            string torrentNginxDependency = m.IncludeTorrent ? "\n      - cloud-torrent" : "";
            sections.Add($$"""
  # === Reverse-proxy (единственная точка выхода наружу) ===
  cloud-nginx:
    image: {{InfraImage("NGINX", "nginx:latest")}}
{{InfraBuild("NGINX")}}
    container_name: ${NGINX_CONTAINER_NAME}
    restart: always{{InfraControls("NGINX")}}{{InfraEnvSection("NGINX")}}
    # Наружу выставлены только эти порты; микросервисы доступны лишь через прокси
    ports:
{{string.Join("\n", nginxPorts)}}
    volumes:
      - ./nginx/cloud.barkfluff.conf:/etc/nginx/conf.d/cloud.barkfluff.conf:ro
      - ./certs:/etc/nginx/certs:ro{{InfraVolume("NGINX")}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-identity
      - cloud-users
      - cloud-files
      - cloud-web{{torrentNginxDependency}}
""");
        }

        if (m.IncludeSeq || m.IncludeMinio || m.IncludeRabbitmq || m.IncludePostgres)
            sections.Add("  # === Инфраструктурные сервисы ===");

        if (m.IncludeSeq)
            sections.Add($$"""
  cloud-seq:
    image: {{InfraImage("SEQ", "datalust/seq:latest")}}
{{InfraBuild("SEQ")}}
    container_name: ${SEQ_CONTAINER_NAME}
    restart: always{{InfraControls("SEQ")}}
    environment:
      ACCEPT_EULA: "Y"
      SEQ_FIRSTRUN_ADMINPASSWORD: "${SEQ_ADMIN_PASSWORD}"{{InfraEnv("SEQ")}}
    volumes:
      - ${SEQ_DATA_PATH:-seq_data}:/data{{InfraVolume("SEQ")}}
    networks:
      - barkcloud-network
""");

        if (m.IncludeMinio)
            sections.Add($$"""
  cloud-minio:
    image: {{InfraImage("MINIO", "docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1")}}
{{InfraBuild("MINIO")}}
    container_name: ${MINIO_CONTAINER_NAME}
    restart: always{{InfraControls("MINIO")}}
    environment:
      MINIO_ROOT_USER: "${MINIO_ROOT_USER}"
      MINIO_ROOT_PASSWORD: "${MINIO_ROOT_PASSWORD}"{{InfraEnv("MINIO")}}
    volumes:
      - ${MINIO_DATA_PATH:-cloud-minio_data}:/data{{InfraVolume("MINIO")}}
    command: server /data --console-address ":9001"
    networks:
      - barkcloud-network
""");

        if (m.IncludeRabbitmq)
            sections.Add($$"""
  cloud-rabbitmq:
    image: {{InfraImage("RABBITMQ", "rabbitmq:latest")}}
{{InfraBuild("RABBITMQ")}}
    container_name: ${RABBITMQ_CONTAINER_NAME}
    restart: always{{InfraControls("RABBITMQ")}}
    volumes:
      - cloud-rabbitmq_data:/var/lib/rabbitmq{{InfraVolume("RABBITMQ")}}
    environment:
      RABBITMQ_DEFAULT_USER: "${RABBITMQ_DEFAULT_USER}"
      RABBITMQ_DEFAULT_PASS: "${RABBITMQ_DEFAULT_PASS}"{{InfraEnv("RABBITMQ")}}
    networks:
      - barkcloud-network
""");

        if (m.IncludePostgres)
            sections.Add($$"""
  cloud-postgres:
    image: {{InfraImage("POSTGRES", "postgres:18")}}
{{InfraBuild("POSTGRES")}}
    container_name: ${POSTGRES_CONTAINER_NAME}
    restart: always{{InfraControls("POSTGRES")}}
    environment:
      POSTGRES_USER: "${POSTGRES_USER}"
      POSTGRES_PASSWORD: "${POSTGRES_PASSWORD}"
      POSTGRES_DB: "${POSTGRES_DB}"
      PGDATA: /var/lib/postgresql/data/pgdata{{InfraEnv("POSTGRES")}}
    command:
      - "postgres"
      - "-c"
      - "shared_buffers=1GB"
      - "-c"
      - "effective_cache_size=3GB"
      - "-c"
      - "work_mem=16MB"
      - "-c"
      - "maintenance_work_mem=256MB"
      - "-c"
      - "max_connections=100"
      - "-c"
      - "wal_buffers=16MB"
      - "-c"
      - "min_wal_size=1GB"
      - "-c"
      - "max_wal_size=4GB"
      - "-c"
      - "checkpoint_completion_target=0.9"
      - "-c"
      - "random_page_cost=1.1"
      - "-c"
      - "effective_io_concurrency=200"
      - "-c"
      - "max_worker_processes=2"
      - "-c"
      - "max_parallel_workers=2"
      - "-c"
      - "max_parallel_workers_per_gather=1"
      - "-c"
      - "jit=off"{{postgresLogConfig}}
    volumes:
      - ${POSTGRES_DATA_PATH:-pgdata}:/var/lib/postgresql
      - ${BACKUP_PATH:-backup_volume}:/backup{{InfraVolume("POSTGRES")}}
    networks:
      - barkcloud-network
""");

        var sb = new StringBuilder();
        sb.Append(string.Join("\n\n", sections));

        if (m.IncludeLognara)
            sb.Append("\n\n").Append(LognaraRelayService);

        // Сети + тома (объявляем только используемые named-тома; порядок — как в исходнике).
        sb.Append("\n\n");
        sb.Append("""
networks:
  barkcloud-network:
    external: true
    name: ${DOCKER_NETWORK_NAME}
""");

        sb.Append("\nvolumes:\n");
        if (m.IncludePostgres) sb.Append("  pgdata:\n");
        if (m.IncludeRabbitmq) sb.Append("  cloud-rabbitmq_data:\n");
        sb.Append("  cloud-minio_data:\n");
        if (m.IncludePostgres) sb.Append("  backup_volume:\n");
        if (m.IncludeSeq) sb.Append("  seq_data:\n");
        sb.Append("  archive_temp:\n");
        if (m.IncludeTorrent) sb.Append("  torrent_data:\n");
        sb.Append("  cloud-web-maintenance:\n");
        if (m.IncludeLognara)
        {
            foreach (var application in m.LognaraApplications
                         .Where(x => m.IsApplicationIncluded(x) && x.LognaraEnable && string.IsNullOrWhiteSpace(x.SpoolPath)))
                sb.Append("  lognara_").Append(application.Prefix.ToLowerInvariant()).Append("_spool:\n");

            foreach (var service in m.LognaraInfrastructure
                         .Where(x => m.IsInfrastructureIncluded(x) && string.IsNullOrWhiteSpace(x.SpoolPath)))
                sb.Append("  lognara_").Append(service.Prefix.ToLowerInvariant()).Append("_spool:\n");

            sb.Append("  lognara_relay_spool:\n");
        }

        return sb.ToString();
    }

    public static string BuildEnv(BuilderModel m)
    {
        var sb = new StringBuilder();

        void Section(string title)
        {
            sb.Append('\n');
            sb.Append("# ").Append(title).Append('\n');
        }
        void K(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');

        sb.Append("# Сгенерировано BarkCloud.Builder\n");

        Section("Публичный адрес Configuration-сервиса для остальных сервисов");
        // Всегда http:// + имя контейнера + порт конфигурации.
        K("CONFIGURATION_SERVICE_URL", $"http://cloud-configuration:{m.ConfigurationPort}");
        sb.Append('\n');
        // Bootstrap-ключ доступа к ConfigurationApi. Должен совпадать у всех сервисов.
        K("CONFIGURATION_ACCESS_KEY", m.ConfigurationAccessKey);

        Section("Docker-сеть и имена контейнеров");
        K("DOCKER_NETWORK_NAME", m.DockerNetworkName);
        foreach (var container in m.ContainerNames.Where(x => x.Enabled && !string.IsNullOrEmpty(x.EnvironmentVariable)))
            K(container.EnvironmentVariable, container.Name);

        Section("Режим ASP.NET Core");
        K("ASPNETCORE_ENVIRONMENT", m.AspNetCoreEnvironment);

        if (m.IncludeMinio)
        {
            Section("MinIO");
            K("MINIO_ROOT_USER", m.MinioRootUser);
            K("MINIO_ROOT_PASSWORD", m.MinioRootPassword);
            K("MINIO_PORT", m.MinioPort);
            K("MINIO_WEBPORT", m.MinioWebPort);
            K("MINIO_DATA_PATH", m.MinioDataPath);
        }

        if (m.IncludeRabbitmq)
        {
            Section("RabbitMQ");
            K("RABBITMQ_DEFAULT_USER", m.RabbitUser);
            K("RABBITMQ_DEFAULT_PASS", m.RabbitPass);
        }

        if (m.IncludePostgres)
        {
            Section("Postgres");
            K("POSTGRES_USER", m.PostgresUser);
            K("POSTGRES_PASSWORD", m.PostgresPassword);
            K("POSTGRES_DB", m.PostgresDb);
            K("POSTGRES_PORT", m.PostgresPort);
            K("POSTGRES_DATA_PATH", m.PostgresDataPath);
            K("BACKUP_PATH", m.BackupPath);
        }

        Section("Порты сервисов");
        K("IDENTITY_PORT", m.IdentityPort);
        K("USERS_PORT", m.UsersPort);
        K("CONFIGURATION_PORT", m.ConfigurationPort);
        K("FILES_PORT", m.FilesPort);
        K("FILES_HTTP1PORT", m.FilesHttp1Port);
        K("TORRENT_PORT", m.TorrentPort);
        K("TORRENT_HTTP1PORT", m.TorrentHttp1Port);
        K("TORRENT_PEER_PORT", m.TorrentPeerPort);

        Section("Torrent — папка на хосте для скачанных торрентов (пусто — named volume torrent_data)");
        K("TORRENT_DOWNLOAD_PATH", m.TorrentDownloadPath);

        Section("Files — внешняя папка для временных ZIP-архивов (пусто — named volume archive_temp)");
        K("ARCHIVE_TEMP_PATH", m.ArchiveTempPath);

        if (m.IncludeSeq)
        {
            Section("Seq (агрегатор логов)");
            K("SEQ_ADMIN_PASSWORD", m.SeqAdminPassword);
            K("SEQ_WEBPORT", m.SeqWebPort);
            K("SEQ_DATA_PATH", m.SeqDataPath);
        }

        Section("Независимые каналы логирования сервисов");
        foreach (var application in m.LognaraApplications)
        {
            K($"{application.Prefix}_CONSOLE_OUTPUT", application.ConsoleOutput ? "true" : "false");
            K($"{application.Prefix}_SEQ_ENABLE", application.SeqEnable ? "true" : "false");
            K($"{application.Prefix}_LOGNARA_ENABLE", application.LognaraEnable ? "true" : "false");
        }

        if (m.IncludeLognara)
        {
            Section("Lognara relay и метаданные агентов");
            K("LOGNARA_CORE_URL", m.LognaraCoreUrl);
            K("LOGNARA_RELAY_CORE_TOKEN", m.LognaraCoreToken);
            K("LOGNARA_RELAY_MEMORY_MB", "2048");
            K("LOGNARA_AGENT_VERSION", string.IsNullOrWhiteSpace(m.LognaraAgentVersion) ? "latest" : m.LognaraAgentVersion);
            K("LOGNARA_SERVER", m.LognaraServer);
            K("LOGNARA_BACKEND", m.LognaraBackend);
            if (!string.IsNullOrWhiteSpace(m.LognaraEnvironment))
                K("LOGNARA_ENVIRONMENT", m.LognaraEnvironment);

            foreach (var application in m.LognaraApplications
                         .Where(x => m.IsApplicationIncluded(x) && x.LognaraEnable))
            {
                K(application.TokenVariable, application.Token);
                K(application.SpoolVariable, application.SpoolPath);
            }

            foreach (var service in m.LognaraInfrastructure.Where(m.IsInfrastructureIncluded))
            {
                K(service.TokenVariable, service.Token);
                K(service.SpoolVariable, service.SpoolPath);
            }
        }

        if (m.UseNginx && m.NginxSeqProxy && m.IncludeSeq)
        {
            Section("Nginx — прокси Seq");
            K("NGINX_SEQ_PORT", m.NginxSeqPort);
        }

        if (m.UseNginx && m.NginxMinioConsoleProxy && m.IncludeMinio)
        {
            Section("Nginx — прокси MinIO Console");
            K("NGINX_MINIO_CONSOLE_PORT", m.NginxMinioConsolePort);
        }

        Section("Веб-клиент");
        K("WEB_PORT", m.WebPort);
        K("WEB_COOKIE_SECURE", m.WebCookieSecure ? "true" : "false");
        K("WEB_PUBLIC_HOST", m.WebPublicHost);
        K("WEB_ADMIN_PASSWORD", m.WebAdminPassword);

        Section("Внешние адреса сервисов для клиентов (обязательны)");
        K("EXTERNAL_IDENTITY_HOST", m.ExternalIdentityHost);
        K("EXTERNAL_USERS_HOST", m.ExternalUsersHost);
        K("EXTERNAL_FILES_HOST", m.ExternalFilesHost);
        K("EXTERNAL_TORRENT_HOST", m.ExternalTorrentHost);

        if (m.IncludeNotification)
        {
            Section("Почта SMTP (опционально; заполните все 4 поля, чтобы включить подтверждение по email)");
            K("EMAIL_HOST", m.EmailHost);
            K("EMAIL_PORT", m.EmailPort);
            K("EMAIL_SENDER_EMAIL", m.EmailSenderEmail);
            K("EMAIL_SENDER_PASSWORD", m.EmailSenderPassword);
        }

        return sb.ToString();
    }

    public static IReadOnlyDictionary<string, string> BuildLognaraDockerfiles(BuilderModel m)
    {
        if (!m.IncludeLognara)
            return new Dictionary<string, string>();

        var definitions = new (string Prefix, string File, string Image, string Entrypoint, string? Command)[]
        {
            ("NGINX", "nginx.Dockerfile", "nginx:latest", "/docker-entrypoint.sh", "[\"nginx\", \"-g\", \"daemon off;\"]"),
            ("MINIO", "minio.Dockerfile", "docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1", "/usr/bin/docker-entrypoint.sh", "[\"minio\"]"),
            ("POSTGRES", "postgres.Dockerfile", "postgres:18", "docker-entrypoint.sh", "[\"postgres\"]"),
            ("RABBITMQ", "rabbitmq.Dockerfile", "rabbitmq:latest", "docker-entrypoint.sh", "[\"rabbitmq-server\"]"),
            ("SEQ", "seq.Dockerfile", "datalust/seq:latest", "/bin/seqentry", null),
        };

        return definitions
            .Where(x => m.IsInfrastructureIncluded(m.LognaraInfrastructure.Single(y => y.Prefix == x.Prefix)))
            .ToDictionary(
                x => x.File,
                x => InfrastructureDockerfile(x.Image, x.Entrypoint, x.Command),
                StringComparer.Ordinal);
    }

    private const string LognaraRelayService = """
  lognara-relay:
    image: ghcr.io/liis17/lognara-relay:master
    container_name: lognara-relay
    restart: always
    environment:
      LOGNARA_LISTEN_ADDR: "0.0.0.0:7401"
      LOGNARA_CORE_URL: "${LOGNARA_CORE_URL}"
      LOGNARA_CORE_TOKEN: "${LOGNARA_RELAY_CORE_TOKEN}"
      LOGNARA_RELAY_MEMORY_MB: "${LOGNARA_RELAY_MEMORY_MB:-2048}"
      LOGNARA_SPOOL_DIR: /var/lib/lognara-relay/spool
    mem_limit: ${LOGNARA_RELAY_MEMORY_MB:-2048}m
    volumes:
      - lognara_relay_spool:/var/lib/lognara-relay/spool
    networks:
      - barkcloud-network
""";

    private static string ApplicationLoggingEnvironment(BuilderModel model, string prefix)
    {
        var sb = new StringBuilder();
        sb.Append("\n      ConsoleOutput: ${").Append(prefix).Append("_CONSOLE_OUTPUT}")
            .Append("\n      SeqEnable: ${").Append(prefix).Append("_SEQ_ENABLE}")
            .Append("\n      LognaraEnable: ${").Append(prefix).Append("_LOGNARA_ENABLE}");

        var application = model.LognaraApplications.Single(x => x.Prefix == prefix);
        if (model.IncludeLognara && model.IsApplicationIncluded(application) && application.LognaraEnable)
            AppendAgentEnvironment(sb, model, prefix, $"{prefix}_CONTAINER_NAME", includeListener: true);

        return sb.ToString();
    }

    private static string ApplicationAgentControls(BuilderModel model, string prefix)
    {
        var application = model.LognaraApplications.Single(x => x.Prefix == prefix);
        return model.IncludeLognara && model.IsApplicationIncluded(application) && application.LognaraEnable
            ? "\n    init: true\n    stop_grace_period: 75s"
            : "";
    }

    private static string ApplicationAgentVolume(BuilderModel model, string prefix)
    {
        var application = model.LognaraApplications.Single(x => x.Prefix == prefix);
        return model.IncludeLognara && model.IsApplicationIncluded(application) && application.LognaraEnable
            ? $"\n      - ${{{application.SpoolVariable}:-lognara_{prefix.ToLowerInvariant()}_spool}}:/var/lib/lognara-agent/spool"
            : "";
    }

    private static string ApplicationAgentVolumeSection(BuilderModel model, string prefix)
    {
        var volume = ApplicationAgentVolume(model, prefix);
        return volume.Length == 0 ? "" : "\n    volumes:" + volume;
    }

    private static string InfrastructureImage(BuilderModel model, string prefix, string original)
    {
        var service = model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
        return model.IncludeLognara && model.IsInfrastructureIncluded(service)
            ? $"barkcloud-lognara-{prefix.ToLowerInvariant()}:latest"
            : original;
    }

    private static string InfrastructureBuild(BuilderModel model, string prefix)
    {
        var service = model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
        return !model.IncludeLognara || !model.IsInfrastructureIncluded(service)
            ? ""
            : $"    build:\n      context: .\n      dockerfile: lognara/{prefix.ToLowerInvariant()}.Dockerfile\n      args:\n        LOGNARA_AGENT_VERSION: ${{LOGNARA_AGENT_VERSION:-latest}}";
    }

    private static string InfrastructureAgentControls(BuilderModel model, string prefix)
    {
        var service = model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
        return model.IncludeLognara && model.IsInfrastructureIncluded(service)
            ? "\n    init: true\n    stop_grace_period: 30s"
            : "";
    }

    private static string InfrastructureAgentEnvironment(BuilderModel model, string prefix)
    {
        var service = model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
        if (!model.IncludeLognara || !model.IsInfrastructureIncluded(service))
            return "";

        var sb = new StringBuilder();
        AppendAgentEnvironment(sb, model, prefix, $"{prefix}_CONTAINER_NAME", includeListener: true);
        return sb.ToString();
    }

    private static void AppendAgentEnvironment(StringBuilder sb, BuilderModel model, string prefix, string containerNameVariable, bool includeListener)
    {
        sb.Append("\n      ");
        if (prefix is "CONFIGURATION" or "IDENTITY" or "USERS" or "FILES" or "NOTIFICATION" or "TORRENT" or "WEB")
            sb.Append("Lognara__AgentUrl: \"http://127.0.0.1:7400/v1/logs\"\n      ");
        if (includeListener)
            sb.Append("LOGNARA_LISTEN_ADDR: \"127.0.0.1:7400\"\n      ");

        sb.Append("LOGNARA_SPOOL_DIR: /var/lib/lognara-agent/spool")
            .Append("\n      LOGNARA_SERVICE: ${").Append(containerNameVariable).Append("}")
            .Append("\n      LOGNARA_SERVER: ${LOGNARA_SERVER}")
            .Append("\n      LOGNARA_BACKEND: ${LOGNARA_BACKEND}");

        if (!string.IsNullOrWhiteSpace(model.LognaraEnvironment))
            sb.Append("\n      LOGNARA_ENVIRONMENT: ${LOGNARA_ENVIRONMENT}");

        var tokenVariable = prefix is "CONFIGURATION" or "IDENTITY" or "USERS" or "FILES" or "NOTIFICATION" or "TORRENT" or "WEB"
            ? model.LognaraApplications.Single(x => x.Prefix == prefix).TokenVariable
            : model.LognaraInfrastructure.Single(x => x.Prefix == prefix).TokenVariable;

        sb.Append("\n      LOGNARA_RELAY_URL: \"http://lognara-relay:7401/v1/batches\"")
            .Append("\n      LOGNARA_RELAY_TOKEN: ${").Append(tokenVariable).Append("}");
    }

    private static string InfrastructureAgentVolume(BuilderModel model, string prefix)
    {
        var service = model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
        return model.IncludeLognara && model.IsInfrastructureIncluded(service)
            ? $"\n      - ${{{service.SpoolVariable}:-lognara_{prefix.ToLowerInvariant()}_spool}}:/var/lib/lognara-agent/spool"
            : "";
    }

    private static string InfrastructureDockerfile(string baseImage, string entrypoint, string? command)
    {
        const string template = """
# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM alpine:3.22 AS lognara-agent
ARG TARGETARCH
ARG LOGNARA_AGENT_VERSION=latest
WORKDIR /download
RUN apk add --no-cache curl jq
RUN set -eu; \
    case "$TARGETARCH" in amd64|arm64) ;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1;; esac; \
    if [ "$LOGNARA_AGENT_VERSION" = "latest" ]; then \
      page=1; tag=""; \
      while :; do \
        releases="$(curl -fSL --retry 3 "https://api.github.com/repos/Liis17/Lognara/releases?per_page=100&page=$page")"; \
        count="$(printf '%s' "$releases" | jq 'if type == "array" then length else 0 end')"; \
        [ "$count" -gt 0 ] || break; \
        tag="$(printf '%s' "$releases" | jq -r '[.[] | select(.draft == false and .prerelease == false) | select(.tag_name | test("^agent-[0-9]{8}-[0-9]{6}-[0-9a-f]{12}$"))][0].tag_name // empty')"; \
        [ -z "$tag" ] || break; page=$((page + 1)); \
      done; \
    else tag="$LOGNARA_AGENT_VERSION"; fi; \
    [ -n "$tag" ] || { echo "No published Lognara agent release found" >&2; exit 1; }; \
    base="https://github.com/Liis17/Lognara/releases/download/$tag"; \
    archive="lognara-agent-linux-$TARGETARCH.tar.gz"; \
    curl -fSL --retry 3 "$base/$archive" -o "$archive"; \
    curl -fSL --retry 3 "$base/SHA256SUMS" -o SHA256SUMS; \
    grep -E "^[0-9a-fA-F]{64}  ${archive}$" SHA256SUMS > selected.sha256; \
    test "$(wc -l < selected.sha256)" -eq 1; sha256sum -c selected.sha256; \
    tar -xzf "$archive"; test -f lognara-agent; chmod 755 lognara-agent

FROM __BASE_IMAGE__
COPY --from=lognara-agent /download/lognara-agent /usr/local/bin/lognara-agent
RUN mkdir -p /var/lib/lognara-agent/spool && chown 0:0 /var/lib/lognara-agent/spool
ENTRYPOINT ["/usr/local/bin/lognara-agent", "run", "--", "__ORIGINAL_ENTRYPOINT__"]
__ORIGINAL_COMMAND__
""";

        return template
            .Replace("__BASE_IMAGE__", baseImage, StringComparison.Ordinal)
            .Replace("__ORIGINAL_ENTRYPOINT__", entrypoint, StringComparison.Ordinal)
            .Replace("__ORIGINAL_COMMAND__", command is null ? "" : $"\nCMD {command}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Генерирует конфиг для встроенного или внешнего nginx с динамическим DNS Docker.
    /// </summary>
    public static string BuildNginxConf(BuilderModel m)
    {
        string domain = string.IsNullOrWhiteSpace(m.NginxDomain) ? "cloud.barkfluff.com" : m.NginxDomain.Trim();
        string crt = m.NginxLocation == "External"
            ? m.ExternalNginxCertificatePath.Trim()
            : "/etc/nginx/certs/" + (string.IsNullOrWhiteSpace(m.CertCrtPath) ? "barkfluff.com-crt.pem" : Path.GetFileName(m.CertCrtPath));
        string key = m.NginxLocation == "External"
            ? m.ExternalNginxKeyPath.Trim()
            : "/etc/nginx/certs/" + (string.IsNullOrWhiteSpace(m.CertKeyPath) ? "barkfluff.com-key.pem" : Path.GetFileName(m.CertKeyPath));

        string Upstream(string name, string containerName, string port, string keepalive) => $$"""
        upstream {{name}} {
            zone {{name}} 64k;
            resolver 127.0.0.11 valid=5s;
            resolver_timeout 2s;
            server {{containerName}}:{{port}} resolve;
        {{keepalive}}
        }
        """;

        string tls = string.Join('\n',
            $"    ssl_certificate     {crt};",
            $"    ssl_certificate_key {key};",
            "    ssl_protocols       TLSv1.2 TLSv1.3;",
            "    ssl_ciphers         HIGH:!aNULL:!MD5;",
            "    ssl_prefer_server_ciphers on;",
            "    ssl_session_cache   shared:barkcloud_ssl:10m;",
            "    ssl_session_timeout 10m;");

        string GrpcServer(string title, string port, string upstream) => $$"""
        # --- {{title}} (порт {{port}}) ---
        server {
            listen {{port}} ssl;
            http2 on;
            server_name {{domain}};

        {{tls}}
            location / {
                grpc_pass grpc://{{upstream}};
                grpc_set_header Host $host;
                grpc_set_header X-Real-IP $remote_addr;
                grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                grpc_set_header X-Forwarded-Proto $scheme;
                grpc_read_timeout 300s;
                grpc_send_timeout 300s;
            }
        }
        """;

        string filesHttp = $$"""
            # HTTP-веб: /web/upload/{id}, /web/download/{id} → Files HTTP/1.
            location /web/ {
                rewrite ^/web/(.*) /$1 break;
                proxy_pass http://barkcloud_files_http;
                proxy_http_version 1.1;
                proxy_set_header Connection "";
                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;
                proxy_request_buffering off;
                proxy_buffering off;
                proxy_read_timeout 7200s;
                proxy_send_timeout 7200s;
                client_body_timeout 7200s;
            }
        """;

        string torrentServer = "";
        if (m.IncludeTorrent)
        {
            torrentServer = $$"""
            # --- Torrent (порт {{m.TorrentPort}}): gRPC + HTTP/1 стриминг ---
            server {
                listen {{m.TorrentPort}} ssl;
                http2 on;
                server_name {{domain}};

            {{tls}}
                client_max_body_size 0;

                location / {
                    grpc_pass grpc://barkcloud_torrent;
                    grpc_set_header Host $host;
                    grpc_set_header X-Real-IP $remote_addr;
                    grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                    grpc_set_header X-Forwarded-Proto $scheme;
                    grpc_read_timeout 300s;
                    grpc_send_timeout 300s;
                }

                # HTTP-веб: /web/download/{id} → Torrent HTTP/1.
                location /web/ {
                    rewrite ^/web/(.*) /$1 break;
                    proxy_pass http://barkcloud_torrent_http;
                    proxy_http_version 1.1;
                    proxy_set_header Connection "";
                    proxy_set_header Host $host;
                    proxy_set_header X-Real-IP $remote_addr;
                    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                    proxy_set_header X-Forwarded-Proto $scheme;
                    proxy_buffering off;
                    proxy_read_timeout 7200s;
                    proxy_send_timeout 7200s;
                }
            }
            """;
        }

        string webServer = $$"""
        # --- Web (порт 443) ---
        server {
            listen 443 ssl;
            server_name {{domain}};
            client_max_body_size 0;

        {{tls}}
            # Upload 2.0: байты напрямую стримятся в HTTP/1 Files.
            location /file-upload/ {
                proxy_pass http://barkcloud_files_http;
                proxy_http_version 1.1;
                proxy_set_header Connection "";
                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;
                proxy_request_buffering off;
                proxy_buffering off;
                proxy_read_timeout 7200s;
                proxy_send_timeout 7200s;
                client_body_timeout 7200s;
            }

            location / {
                proxy_pass http://barkcloud_web;
                proxy_set_header Host $host;
                proxy_set_header X-Forwarded-Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;
                proxy_cookie_path / "/; secure; HttpOnly; SameSite=strict";
                proxy_redirect off;
                proxy_http_version 1.1;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection "upgrade";
                proxy_request_buffering off;
                proxy_buffering off;
                proxy_read_timeout 7200s;
                proxy_send_timeout 7200s;
                client_body_timeout 7200s;
            }
        }
        """;

        string consoleHeaders = """
                    proxy_set_header Host $http_host;
                    proxy_set_header X-Forwarded-Host $http_host;
                    proxy_set_header X-Forwarded-Port $server_port;
                    proxy_set_header X-Real-IP $remote_addr;
                    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                    proxy_set_header X-Forwarded-Proto $scheme;
            """;

        string seqServer = m.NginxSeqProxy && m.IncludeSeq
            ? $$"""
            # --- Seq Console ---
            server {
                listen {{m.NginxSeqPort}} ssl;
                server_name {{domain}};

            {{tls}}
                location / {
                    proxy_pass http://barkcloud_seq;
                    proxy_http_version 1.1;
            {{consoleHeaders}}
                    proxy_read_timeout 300s;
                    proxy_send_timeout 300s;
                }
            }
            """
            : "";

        string minioServer = m.NginxMinioConsoleProxy && m.IncludeMinio
            ? $$"""
            # --- MinIO Console ---
            server {
                listen {{m.NginxMinioConsolePort}} ssl;
                server_name {{domain}};

            {{tls}}
                client_max_body_size 0;
                location / {
                    proxy_pass http://barkcloud_minio_console;
                    proxy_http_version 1.1;
                    proxy_set_header Upgrade $http_upgrade;
                    proxy_set_header Connection "upgrade";
            {{consoleHeaders}}
                    proxy_request_buffering off;
                    proxy_buffering off;
                    proxy_read_timeout 7200s;
                    proxy_send_timeout 7200s;
                    client_body_timeout 7200s;
                }
            }
            """
            : "";

        var sections = new List<string>
        {
            $"# BarkCloud — единый субдомен {domain}, маршрутизация по порту.",
            Upstream("barkcloud_identity", m.IdentityContainerName, m.IdentityPort, "    keepalive 32;\n    keepalive_requests 1000;\n    keepalive_timeout 60s;"),
            Upstream("barkcloud_users", m.UsersContainerName, m.UsersPort, "    keepalive 32;\n    keepalive_requests 1000;\n    keepalive_timeout 60s;"),
            Upstream("barkcloud_files", m.FilesContainerName, m.FilesPort, "    keepalive 32;\n    keepalive_requests 1000;\n    keepalive_timeout 60s;"),
            Upstream("barkcloud_files_http", m.FilesContainerName, m.FilesHttp1Port, "    keepalive 16;\n    keepalive_timeout 60s;"),
            Upstream("barkcloud_web", m.WebContainerName, "8080", "    keepalive 16;\n    keepalive_timeout 60s;"),
            GrpcServer("Identity", m.IdentityPort, "barkcloud_identity"),
            GrpcServer("Users", m.UsersPort, "barkcloud_users"),
            """
            # --- Files: gRPC + HTTP/1 веб ---
            """,
            $$"""
            server {
                listen {{m.FilesPort}} ssl;
                http2 on;
                server_name {{domain}};

            {{tls}}
                client_max_body_size 0;
                location / {
                    grpc_pass grpc://barkcloud_files;
                    grpc_set_header Host $host;
                    grpc_set_header X-Real-IP $remote_addr;
                    grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                    grpc_set_header X-Forwarded-Proto $scheme;
                    grpc_read_timeout 300s;
                    grpc_send_timeout 300s;
                }
            {{filesHttp}}
            }
            """,
        };

        if (m.IncludeTorrent)
        {
            sections.Add(Upstream("barkcloud_torrent", m.TorrentContainerName, m.TorrentPort, "    keepalive 32;\n    keepalive_requests 1000;\n    keepalive_timeout 60s;"));
            sections.Add(Upstream("barkcloud_torrent_http", m.TorrentContainerName, m.TorrentHttp1Port, "    keepalive 16;\n    keepalive_timeout 60s;"));
            sections.Add(torrentServer);
        }

        if (m.NginxSeqProxy && m.IncludeSeq)
        {
            sections.Add(Upstream("barkcloud_seq", m.SeqContainerName, "80", "    keepalive 16;\n    keepalive_timeout 60s;"));
            sections.Add(seqServer);
        }

        if (m.NginxMinioConsoleProxy && m.IncludeMinio)
        {
            sections.Add(Upstream("barkcloud_minio_console", m.MinioContainerName, "9001", "    keepalive 16;\n    keepalive_timeout 60s;"));
            sections.Add(minioServer);
        }

        sections.Add(webServer);
        return string.Join("\n\n", sections.Select(x => x.Trim()));
    }
}
