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

        // Все сервисы доступны только внутри barkcloud-network.
        // Внешние порты публикует reverse-proxy, если он включён.

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
        string torrentNginxPort = m.IncludeTorrent
            ? "\n      - \"${TORRENT_PORT}:${TORRENT_PORT}\""
            : "";

        // Каждый блок-секция не содержит завершающего перевода строки; секции склеиваются
        // через пустую строку, что даёт ровно один разделитель между сервисами.
        var sections = new List<string>
        {
            // Шапка + ядро микросервисов (всегда присутствует).
            $$"""
version: '3.8'

x-common-variables: &common-variables
  CONFIGURATION_SERVICE_URL: "${CONFIGURATION_SERVICE_URL}"
  ASPNETCORE_ENVIRONMENT: "${ASPNETCORE_ENVIRONMENT}"
  CONFIGURATION_ACCESS_KEY: "${CONFIGURATION_ACCESS_KEY}"

services:
  # === Основные микросервисы (prod-образы) ===

  cloud-configuration:
    image: {{Img("configuration")}}
    container_name: cloud-configuration
    restart: always
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
      TORRENT_PEER_PORT: ${TORRENT_PEER_PORT}
    networks:
      - barkcloud-network

  cloud-identity:
    image: {{Img("identity")}}
    container_name: cloud-identity
    restart: always
    environment:
      <<: *common-variables
      SERVICE_PORT: ${IDENTITY_PORT}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration

  cloud-users:
    image: {{Img("users")}}
    container_name: cloud-users
    restart: always
    environment:
      <<: *common-variables
      SERVICE_PORT: ${USERS_PORT}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration

  cloud-files:
    image: {{Img("files")}}
    container_name: cloud-files
    restart: always
    environment:
      <<: *common-variables
      SERVICE_PORT: ${FILES_PORT}
      SERVICE_HTTP1PORT: ${FILES_HTTP1PORT}
      StorageProbe__Path: "/mnt/minio-data"
      Archive__TempPath: "/mnt/archive-temp"
    volumes:
      - ${MINIO_DATA_PATH:-cloud-minio_data}:/mnt/minio-data:ro
      - ${ARCHIVE_TEMP_PATH:-archive_temp}:/mnt/archive-temp
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
""",
        };

        if (m.IncludeNotification)
            sections.Add($$"""
  # Сервис уведомлений. Внешнего API нет — в nginx не маршрутизируется. Опционален
  cloud-notification:
    image: {{Img("notification")}}
    container_name: cloud-notification
    restart: always
    environment:
      <<: *common-variables
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
""");

        if (m.IncludeTorrent)
        {
            var torrentPorts = "";

            sections.Add($$"""
  # Торрент-сервис: качает торренты на хост-диск ({TORRENT_DOWNLOAD_PATH} → /mnt/torrents)
  cloud-torrent:
    image: {{Img("torrent")}}
    container_name: cloud-torrent
    restart: always
    environment:
      <<: *common-variables
      SERVICE_PORT: ${TORRENT_PORT}
      SERVICE_HTTP1PORT: ${TORRENT_HTTP1PORT}
      Torrent__DownloadPath: "/mnt/torrents"
      Torrent__PeerPort: ${TORRENT_PEER_PORT}
    volumes:
      - ${TORRENT_DOWNLOAD_PATH:-torrent_data}:/mnt/torrents{{torrentPorts}}
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
""");
        }

        // Веб-клиент — всегда (отключить нельзя).
        sections.Add($$"""
  # Веб-клиент
  cloud-web:
    image: {{Img("web")}}
    container_name: cloud-web
    restart: always
    user: root
    env_file:
      - .env
    environment:
      <<: *common-variables
      ASPNETCORE_URLS: "http://+:8080"
      App__CookieSecure: "${WEB_COOKIE_SECURE}"
      App__PublicHost: "${WEB_PUBLIC_HOST}"
      App__AdminPassword: "${WEB_ADMIN_PASSWORD}"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./docker-compose.yml:/docker-compose.yml:rw
      - ./.env:/.env:ro
      - cloud-web-maintenance:/app/maintenance
    networks:
      - barkcloud-network
    depends_on:
      - cloud-configuration
      - cloud-identity
      - cloud-users
      - cloud-files{{torrentDependency}}
""");

        if (m.IncludeNginx)
            sections.Add("""
  # === Reverse-proxy (единственная точка выхода наружу) ===
  cloud-nginx:
    image: nginx:latest
    container_name: cloud-nginx
    restart: always
    # Наружу выставлены только эти порты; микросервисы доступны лишь через прокси
    ports:
      - "443:443"
      - "${IDENTITY_PORT}:${IDENTITY_PORT}"
      - "${USERS_PORT}:${USERS_PORT}"
      - "${FILES_PORT}:${FILES_PORT}"{{torrentNginxPort}}
    volumes:
      - ./nginx/cloud.barkfluff.conf:/etc/nginx/conf.d/cloud.barkfluff.conf:ro
      - ./certs:/etc/nginx/certs:ro
    networks:
      - barkcloud-network
    depends_on:
      - cloud-identity
      - cloud-users
      - cloud-files
      - cloud-web{{torrentDependency}}
""");

        if (m.IncludeSeq || m.IncludeMinio || m.IncludeRabbitmq || m.IncludePostgres)
            sections.Add("  # === Инфраструктурные сервисы ===");

        if (m.IncludeSeq)
            sections.Add("""
  cloud-seq:
    image: datalust/seq:latest
    container_name: cloud-seq
    restart: always
    environment:
      ACCEPT_EULA: "Y"
      SEQ_FIRSTRUN_ADMINPASSWORD: "${SEQ_ADMIN_PASSWORD}"
    volumes:
      - ${SEQ_DATA_PATH:-seq_data}:/data
    networks:
      - barkcloud-network
""");

        if (m.IncludeMinio)
            sections.Add("""
  cloud-minio:
    image: quay.io/minio/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1
    container_name: cloud-minio
    restart: always
    environment:
      MINIO_ROOT_USER: "${MINIO_ROOT_USER}"
      MINIO_ROOT_PASSWORD: "${MINIO_ROOT_PASSWORD}"
    volumes:
      - ${MINIO_DATA_PATH:-cloud-minio_data}:/data
    command: server /data --console-address ":9001"
    networks:
      - barkcloud-network
""");

        if (m.IncludeRabbitmq)
            sections.Add("""
  cloud-rabbitmq:
    image: rabbitmq:latest
    container_name: cloud-rabbitmq
    restart: always
    volumes:
      - cloud-rabbitmq_data:/var/lib/rabbitmq
    environment:
      RABBITMQ_DEFAULT_USER: "${RABBITMQ_DEFAULT_USER}"
      RABBITMQ_DEFAULT_PASS: "${RABBITMQ_DEFAULT_PASS}"
    networks:
      - barkcloud-network
""");

        if (m.IncludePostgres)
            sections.Add("""
  cloud-postgres:
    image: postgres:18
    container_name: cloud-postgres
    restart: always
    environment:
      POSTGRES_USER: "${POSTGRES_USER}"
      POSTGRES_PASSWORD: "${POSTGRES_PASSWORD}"
      POSTGRES_DB: "${POSTGRES_DB}"
      PGDATA: /var/lib/postgresql/data/pgdata
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
      - "jit=off"
    volumes:
      - ${POSTGRES_DATA_PATH:-pgdata}:/var/lib/postgresql
      - ${BACKUP_PATH:-backup_volume}:/backup
    networks:
      - barkcloud-network
""");

        var sb = new StringBuilder();
        sb.Append(string.Join("\n\n", sections));

        // Сети + тома (объявляем только используемые named-тома; порядок — как в исходнике).
        sb.Append("\n\n");
        sb.Append("""
networks:
  barkcloud-network:
    external: true
    name: barkcloud-network

volumes:

""");
        if (m.IncludePostgres) sb.Append("  pgdata:\n");
        if (m.IncludeRabbitmq) sb.Append("  cloud-rabbitmq_data:\n");
        sb.Append("  cloud-minio_data:\n");
        if (m.IncludePostgres) sb.Append("  backup_volume:\n");
        if (m.IncludeSeq) sb.Append("  seq_data:\n");
        sb.Append("  archive_temp:\n");
        if (m.IncludeTorrent) sb.Append("  torrent_data:\n");
        sb.Append("  cloud-web-maintenance:\n");

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

    /// <summary>
    /// nginx/cloud.barkfluff.conf: домен (server_name), порты (listen + upstream) и
    /// имена файлов сертификатов подставляются из полей модели.
    /// </summary>
    public static string BuildNginxConf(BuilderModel m)
    {
        string domain = string.IsNullOrWhiteSpace(m.NginxDomain) ? "cloud.barkfluff.com" : m.NginxDomain.Trim();
        string crt = string.IsNullOrWhiteSpace(m.CertCrtPath) ? "barkfluff.com-crt.pem" : Path.GetFileName(m.CertCrtPath);
        string key = string.IsNullOrWhiteSpace(m.CertKeyPath) ? "barkfluff.com-key.pem" : Path.GetFileName(m.CertKeyPath);

        // Торрент-сервис маршрутизируется только если включён: gRPC на своём порту + HTTP1
        // (/web/download/{id}) для стриминга файлов внешним клиентам (веб ходит напрямую в docker-сеть).
        string torrentServer = !m.IncludeTorrent ? "" : $$"""

# --- Пулы торрент-сервиса ---
upstream barkcloud_torrent {
    server cloud-torrent:{{m.TorrentPort}};
    keepalive 32;
    keepalive_requests 1000;
    keepalive_timeout 60s;
}

upstream barkcloud_torrent_http {
    server cloud-torrent:{{m.TorrentHttp1Port}};
    keepalive 16;
    keepalive_timeout 60s;
}

# --- Torrent (порт {{m.TorrentPort}}): gRPC + HTTP1 стриминг файлов ---
server {
    listen {{m.TorrentPort}} ssl;
    http2 on;
    server_name {{domain}};

    ssl_certificate     /etc/nginx/certs/{{crt}};
    ssl_certificate_key /etc/nginx/certs/{{key}};
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    ssl_session_cache   shared:SSL:10m;
    ssl_session_timeout 10m;

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

    # HTTP-веб: /web/download/{id} -> cloud-torrent:{{m.TorrentHttp1Port}}/download/{id}
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

        return $$"""
# BarkCloud — единый субдомен {{domain}}, маршрутизация по порту.

# --- Пулы соединений к бэкендам (постоянные h2c-соединения для gRPC) ---
upstream barkcloud_identity {
    server cloud-identity:{{m.IdentityPort}};
    keepalive 32;            # держать до 32 idle-соединений в пуле на воркер
    keepalive_requests 1000; # пересоздавать соединение после 1000 запросов
    keepalive_timeout 60s;   # закрыть простаивающее соединение через 60с
}

upstream barkcloud_users {
    server cloud-users:{{m.UsersPort}};
    keepalive 32;
    keepalive_requests 1000;
    keepalive_timeout 60s;
}

upstream barkcloud_files {
    server cloud-files:{{m.FilesPort}};
    keepalive 32;
    keepalive_requests 1000;
    keepalive_timeout 60s;
}

# HTTP/1.1-пул для скачивания/загрузки файлов (/web/).
upstream barkcloud_files_http {
    server cloud-files:{{m.FilesHttp1Port}};
    keepalive 16;
    keepalive_timeout 60s;
}

# --- Identity (порт {{m.IdentityPort}}) ---
server {
    listen {{m.IdentityPort}} ssl;
    http2 on;
    server_name {{domain}};

    ssl_certificate     /etc/nginx/certs/{{crt}};
    ssl_certificate_key /etc/nginx/certs/{{key}};
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    ssl_session_cache   shared:SSL:10m;
    ssl_session_timeout 10m;

    location / {
        grpc_pass grpc://barkcloud_identity;
        grpc_set_header Host $host;
        grpc_set_header X-Real-IP $remote_addr;
        grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        grpc_set_header X-Forwarded-Proto $scheme;
        grpc_read_timeout 300s;
        grpc_send_timeout 300s;
    }
}

# --- Users (порт {{m.UsersPort}}) ---
server {
    listen {{m.UsersPort}} ssl;
    http2 on;
    server_name {{domain}};

    ssl_certificate     /etc/nginx/certs/{{crt}};
    ssl_certificate_key /etc/nginx/certs/{{key}};
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    ssl_session_cache   shared:SSL:10m;
    ssl_session_timeout 10m;

    location / {
        grpc_pass grpc://barkcloud_users;
        grpc_set_header Host $host;
        grpc_set_header X-Real-IP $remote_addr;
        grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        grpc_set_header X-Forwarded-Proto $scheme;
        grpc_read_timeout 300s;
        grpc_send_timeout 300s;
    }
}

# --- Files (порт {{m.FilesPort}}): gRPC + HTTP1 веб для скачивания/загрузки ---
server {
    listen {{m.FilesPort}} ssl;
    http2 on;
    server_name {{domain}};

    ssl_certificate     /etc/nginx/certs/{{crt}};
    ssl_certificate_key /etc/nginx/certs/{{key}};
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    ssl_session_cache   shared:SSL:10m;
    ssl_session_timeout 10m;

    client_max_body_size 0;  # без лимита размера тела (загрузка файлов)

    # gRPC API (основной)
    location / {
        grpc_pass grpc://barkcloud_files;
        grpc_set_header Host $host;
        grpc_set_header X-Real-IP $remote_addr;
        grpc_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        grpc_set_header X-Forwarded-Proto $scheme;
        grpc_read_timeout 300s;
        grpc_send_timeout 300s;
    }

    # HTTP-веб: /web/upload/{id}, /web/download/{id} -> cloud-files:{{m.FilesHttp1Port}}/upload|download
    location /web/ {
        rewrite ^/web/(.*) /$1 break;
        proxy_pass http://barkcloud_files_http;
        # keepalive к апстриму требует HTTP/1.1 и очистки заголовка Connection
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Большие файлы — буферизацию выключаем, таймауты увеличиваем (2 часа)
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 7200s;
        proxy_send_timeout 7200s;
        client_body_timeout 7200s;
    }
}

{{torrentServer}}
# --- Веб-клиент (порт 443): прокси на cloud-web:8080 ---
server {
    listen 443 ssl;
    server_name {{domain}};
    client_max_body_size 0;  # без лимита размера тела (загрузка через браузер)
    ssl_certificate /etc/nginx/certs/{{crt}};
    ssl_certificate_key /etc/nginx/certs/{{key}};

    location / {
        proxy_pass http://cloud-web:8080;
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

        # Загрузка/скачивание больших файлов через браузер: буферизация off, таймауты 2 часа
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 7200s;
        proxy_send_timeout 7200s;
        client_body_timeout 7200s;
    }
}

""";
    }
}
