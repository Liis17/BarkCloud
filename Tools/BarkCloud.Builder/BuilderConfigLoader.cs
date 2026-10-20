using System.IO;
using System.Text.RegularExpressions;

namespace BarkCloud.Builder;

/// <summary>
/// Восстанавливает <see cref="BuilderModel"/> из ранее сгенерированных docker-compose.yml, .env
/// и nginx/cloud.barkfluff.conf. Значения берутся из .env, состав сервисов и канал образов —
/// из compose, домен и пути сертификатов — из nginx-конфига. Отсутствующие ключи оставляют дефолт.
/// </summary>
public static class BuilderConfigLoader
{
    public static BuilderModel Load(string compose, string env, string? nginxConf, string dir)
    {
        // Генератор может вернуть CRLF (исходник с CRLF), файл на диске — LF.
        compose = compose.Replace("\r\n", "\n");
        var e = ParseEnv(env);
        var m = new BuilderModel { OutputPath = dir };

        bool HasService(string name) => compose.Contains($"\n  {name}:\n");
        bool HasComposeText(string value, StringComparison comparison = StringComparison.Ordinal) => compose
            .Split('\n')
            .Any(line => !line.TrimStart().StartsWith('#') && line.Contains(value, comparison));
        m.IncludeNotification = HasService("cloud-notification");
        m.IncludeTorrent = HasService("cloud-torrent");
        m.IncludeMinio = HasService("cloud-minio");
        m.IncludeRabbitmq = HasService("cloud-rabbitmq");
        m.IncludePostgres = HasService("cloud-postgres");
        m.IncludeSeq = HasService("cloud-seq");
        var hasLognaraRelay = HasService("cloud-relay") || HasService("lognara-relay");
        m.LegacyLognaraImport = !hasLognaraRelay
            && (e.ContainsKey("LOGNARA_RELAY_URL")
                || HasComposeText("Lognara__AgentUrl")
                || HasComposeText("lognara-agent", StringComparison.OrdinalIgnoreCase));
        m.IncludeLognara = hasLognaraRelay || m.LegacyLognaraImport;
        m.UseNginx = HasService("cloud-nginx") || nginxConf != null;
        m.NginxLocation = HasService("cloud-nginx") ? "Compose" : "External";
        m.ImageChannel = compose.Contains("barkcloud-configuration-dev:") ? "Dev" : "Release";

        void Set(string key, Action<string> setter)
        {
            if (e.TryGetValue(key, out var value))
                setter(value);
        }

        Set("CONFIGURATION_ACCESS_KEY", v => m.ConfigurationAccessKey = v);
        Set("DOCKER_NETWORK_NAME", v => m.DockerNetworkName = v);
        Set("CONFIGURATION_CONTAINER_NAME", v => m.ConfigurationContainerName = v);
        Set("IDENTITY_CONTAINER_NAME", v => m.IdentityContainerName = v);
        Set("USERS_CONTAINER_NAME", v => m.UsersContainerName = v);
        Set("FILES_CONTAINER_NAME", v => m.FilesContainerName = v);
        Set("NOTIFICATION_CONTAINER_NAME", v => m.NotificationContainerName = v);
        Set("TORRENT_CONTAINER_NAME", v => m.TorrentContainerName = v);
        Set("WEB_CONTAINER_NAME", v => m.WebContainerName = v);
        Set("MINIO_CONTAINER_NAME", v => m.MinioContainerName = v);
        Set("RABBITMQ_CONTAINER_NAME", v => m.RabbitmqContainerName = v);
        Set("POSTGRES_CONTAINER_NAME", v => m.PostgresContainerName = v);
        Set("SEQ_CONTAINER_NAME", v => m.SeqContainerName = v);
        Set("NGINX_CONTAINER_NAME", v => m.NginxContainerName = v);
        Set("ASPNETCORE_ENVIRONMENT", v => m.AspNetCoreEnvironment = v);

        Set("MINIO_ROOT_USER", v => m.MinioRootUser = v);
        Set("MINIO_ROOT_PASSWORD", v => m.MinioRootPassword = v);
        Set("MINIO_PORT", v => m.MinioPort = v);
        Set("MINIO_WEBPORT", v => m.MinioWebPort = v);
        Set("MINIO_DATA_PATH", v => m.MinioDataPath = v);
        Set("RABBITMQ_DEFAULT_USER", v => m.RabbitUser = v);
        Set("RABBITMQ_DEFAULT_PASS", v => m.RabbitPass = v);
        Set("POSTGRES_USER", v => m.PostgresUser = v);
        Set("POSTGRES_PASSWORD", v => m.PostgresPassword = v);
        Set("POSTGRES_DB", v => m.PostgresDb = v);
        Set("POSTGRES_PORT", v => m.PostgresPort = v);
        Set("POSTGRES_DATA_PATH", v => m.PostgresDataPath = v);
        Set("BACKUP_PATH", v => m.BackupPath = v);

        Set("IDENTITY_PORT", v => m.IdentityPort = v);
        Set("USERS_PORT", v => m.UsersPort = v);
        Set("CONFIGURATION_PORT", v => m.ConfigurationPort = v);
        Set("FILES_PORT", v => m.FilesPort = v);
        Set("FILES_HTTP1PORT", v => m.FilesHttp1Port = v);
        Set("TORRENT_PORT", v => m.TorrentPort = v);
        Set("TORRENT_HTTP1PORT", v => m.TorrentHttp1Port = v);
        Set("TORRENT_PEER_PORT", v => m.TorrentPeerPort = v);
        Set("TORRENT_DOWNLOAD_PATH", v => m.TorrentDownloadPath = v);
        Set("ARCHIVE_TEMP_PATH", v => m.ArchiveTempPath = v);

        Set("SEQ_ADMIN_PASSWORD", v => m.SeqAdminPassword = v);
        Set("SEQ_WEBPORT", v => m.SeqWebPort = v);
        Set("SEQ_DATA_PATH", v => m.SeqDataPath = v);

        Set("LOGNARA_CORE_URL", v => m.LognaraCoreUrl = v);
        Set("LOGNARA_RELAY_CORE_TOKEN", v => m.LognaraCoreToken = v);
        Set("LOGNARA_SERVER", v => m.LognaraServer = v);
        Set("LOGNARA_BACKEND", v => m.LognaraBackend = v);
        Set("LOGNARA_ENVIRONMENT", v => m.LognaraEnvironment = v);
        Set("LOGNARA_AGENT_VERSION", v => m.LognaraAgentVersion = v);

        bool hasExplicitLoggingFlags = m.LognaraApplications.Any(application =>
            e.ContainsKey($"{application.Prefix}_CONSOLE_OUTPUT")
            || e.ContainsKey($"{application.Prefix}_SEQ_ENABLE")
            || e.ContainsKey($"{application.Prefix}_LOGNARA_ENABLE"));
        m.LegacySeqConfigurationUnknown = !hasExplicitLoggingFlags && !m.IncludeSeq;

        foreach (var application in m.LognaraApplications)
        {
            Set($"{application.Prefix}_CONSOLE_OUTPUT", v => application.ConsoleOutput = ParseBoolean(v, application.ConsoleOutput));
            Set($"{application.Prefix}_SEQ_ENABLE", v => application.SeqEnable = ParseBoolean(v, application.SeqEnable));
            Set($"{application.Prefix}_LOGNARA_ENABLE", v => application.LognaraEnable = ParseBoolean(v, application.LognaraEnable));
            Set(application.TokenVariable, v => application.Token = v);
            Set(application.SpoolVariable, v => application.SpoolPath = v);
        }

        foreach (var service in m.LognaraInfrastructure)
        {
            Set(service.TokenVariable, v => service.Token = v);
            Set(service.SpoolVariable, v => service.SpoolPath = v);
        }

        // Старые compose не содержали явных app-флагов: Seq соответствовал наличию Seq,
        // а Lognara собирала только Configuration и заменяла его console sink.
        if (!hasExplicitLoggingFlags)
        {
            foreach (var application in m.LognaraApplications)
                application.SeqEnable = m.IncludeSeq;

            if (m.LegacyLognaraImport)
            {
                var configuration = m.LognaraApplications.Single(x => x.Prefix == "CONFIGURATION");
                configuration.ConsoleOutput = false;
                configuration.LognaraEnable = true;
            }
        }

        // Порты прокси консолей пишутся в .env только при включённом прокси.
        m.NginxSeqProxy = e.ContainsKey("NGINX_SEQ_PORT");
        Set("NGINX_SEQ_PORT", v => m.NginxSeqPort = v);
        m.NginxMinioConsoleProxy = e.ContainsKey("NGINX_MINIO_CONSOLE_PORT");
        Set("NGINX_MINIO_CONSOLE_PORT", v => m.NginxMinioConsolePort = v);

        Set("WEB_PORT", v => m.WebPort = v);
        Set("WEB_COOKIE_SECURE", v => m.WebCookieSecure = v.Equals("true", StringComparison.OrdinalIgnoreCase));
        Set("WEB_PUBLIC_HOST", v => m.WebPublicHost = v);
        Set("WEB_ADMIN_PASSWORD", v => m.WebAdminPassword = v);

        Set("EXTERNAL_IDENTITY_HOST", v => m.ExternalIdentityHost = v);
        Set("EXTERNAL_USERS_HOST", v => m.ExternalUsersHost = v);
        Set("EXTERNAL_FILES_HOST", v => m.ExternalFilesHost = v);
        Set("EXTERNAL_TORRENT_HOST", v => m.ExternalTorrentHost = v);

        Set("EMAIL_HOST", v => m.EmailHost = v);
        Set("EMAIL_PORT", v => m.EmailPort = v);
        Set("EMAIL_SENDER_EMAIL", v => m.EmailSenderEmail = v);
        Set("EMAIL_SENDER_PASSWORD", v => m.EmailSenderPassword = v);

        if (nginxConf != null)
            ApplyNginxConf(m, nginxConf, dir);

        return m;
    }

    private static void ApplyNginxConf(BuilderModel m, string conf, string dir)
    {
        var domain = Regex.Match(conf, @"^\s*server_name\s+([^;\s]+)\s*;", RegexOptions.Multiline);
        if (domain.Success)
            m.NginxDomain = domain.Groups[1].Value;

        var crt = Regex.Match(conf, @"^\s*ssl_certificate\s+([^;]+?)\s*;", RegexOptions.Multiline);
        var key = Regex.Match(conf, @"^\s*ssl_certificate_key\s+([^;]+?)\s*;", RegexOptions.Multiline);

        if (m.NginxLocation == "External")
        {
            if (crt.Success) m.ExternalNginxCertificatePath = crt.Groups[1].Value;
            if (key.Success) m.ExternalNginxKeyPath = key.Groups[1].Value;
            return;
        }

        // Compose: сертификаты лежат в certs/ рядом с compose; несуществующий файл — пустое поле.
        string LocalCert(System.Text.RegularExpressions.Match match)
        {
            if (!match.Success)
                return "";
            var path = Path.Combine(dir, "certs", Path.GetFileName(match.Groups[1].Value));
            return File.Exists(path) ? path : "";
        }

        m.CertCrtPath = LocalCert(crt);
        m.CertKeyPath = LocalCert(key);
    }

    private static Dictionary<string, string> ParseEnv(string env)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in env.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            if (eq > 0)
                result[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return result;
    }

    private static bool ParseBoolean(string value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;
}
