using System.IO;
using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class BuilderGeneratorTests
{
    [Fact]
    public void Default_external_mode_generates_config_without_nginx_service_or_host_ports()
    {
        var model = new BuilderModel();

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);
        var nginx = BackendComposeGenerator.BuildNginxConf(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.DoesNotContain("  cloud-nginx:", compose);
        Assert.DoesNotContain("NGINX_CONTAINER_NAME=", env);
        Assert.Contains("DOCKER_NETWORK_NAME=barkcloud-network", env);
        Assert.Contains("name: ${DOCKER_NETWORK_NAME}", compose);
        Assert.Contains("container_name: ${WEB_CONTAINER_NAME}", compose);
        Assert.Contains("Docker__ContainerNames__Web: \"${WEB_CONTAINER_NAME}\"", compose);
        Assert.Contains("image: docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1", compose);
        Assert.Contains("CONFIGURATION_SERVICE_URL=http://cloud-configuration:8003", env);
        Assert.Contains("IDENTITY_PORT=8000", env);
        Assert.Contains("USERS_PORT=8001", env);
        Assert.Contains("FILES_PORT=8005", env);
        Assert.Contains("FILES_HTTP1PORT=8006", env);
        Assert.Contains("TORRENT_PORT=8007", env);
        Assert.Contains("TORRENT_HTTP1PORT=8008", env);
        Assert.Contains("MINIO_PORT=9000", env);
        Assert.Contains("MINIO_WEBPORT=9001", env);
        Assert.Contains("POSTGRES_PORT=5432", env);
        Assert.DoesNotContain("SEQ_WEBPORT=", env);
        Assert.Contains("WEB_PORT=8080", env);
        Assert.Contains("MINIO_DATA_PATH=./data/minio", env);
        Assert.Contains("POSTGRES_DATA_PATH=./data/pgdata", env);
        Assert.Contains("BACKUP_PATH=./data/pgbackup", env);
        Assert.DoesNotContain("SEQ_DATA_PATH=", env);
        Assert.Contains("CONFIGURATION_CONSOLE_OUTPUT=true", env);
        Assert.Contains("CONFIGURATION_SEQ_ENABLE=false", env);
        Assert.Contains("CONFIGURATION_LOGNARA_ENABLE=false", env);
        Assert.Contains("TORRENT_DOWNLOAD_PATH=./data/torrent", env);
        Assert.Contains("ARCHIVE_TEMP_PATH=./data/archive-temp", env);
        Assert.Contains("server cloud-web:8080 resolve;", nginx);
        Assert.Contains("server cloud-identity:8000 resolve;", nginx);
        Assert.Contains("server cloud-users:8001 resolve;", nginx);
        Assert.Contains("server cloud-files:8005 resolve;", nginx);
        Assert.Contains("server cloud-files:8006 resolve;", nginx);
        Assert.Contains("server cloud-torrent:8007 resolve;", nginx);
        Assert.Contains("server cloud-torrent:8008 resolve;", nginx);
        Assert.Contains("listen 8000 ssl;", nginx);
        Assert.Contains("listen 8001 ssl;", nginx);
        Assert.Contains("listen 8005 ssl;", nginx);
        Assert.Contains("listen 8007 ssl;", nginx);
        Assert.Contains("resolver 127.0.0.11 valid=5s;", nginx);
        Assert.Contains("resolver_timeout 2s;", nginx);
        Assert.Contains("zone barkcloud_web 64k;", nginx);
        Assert.Contains("ssl_session_cache   shared:barkcloud_ssl:10m;", nginx);
        Assert.Contains("rewrite ^/web/(.*) /$1 break;", nginx);
        Assert.Contains("location /file-upload/", nginx);
        Assert.Contains("proxy_set_header Upgrade $http_upgrade;", nginx);
        Assert.Contains("proxy_request_buffering off;", nginx);
        Assert.Contains("proxy_read_timeout 7200s;", nginx);
        Assert.DoesNotContain("proxy_pass http://cloud-web:8080", nginx);
        Assert.DoesNotMatch(@"(?m)^[ \t]*listen[ \t]+80(?:[ \t]|;)", nginx);
        Assert.DoesNotContain("{{", nginx);
        Assert.DoesNotContain("}}", nginx);
        Assert.DoesNotContain("NGINX_SEQ_PORT", env);
        Assert.DoesNotContain("NGINX_MINIO_CONSOLE_PORT", env);
    }

    [Fact]
    public void Empty_data_paths_are_written_as_empty_and_use_named_volume_fallbacks()
    {
        var model = new BuilderModel
        {
            IncludeSeq = true,
            MinioDataPath = "",
            PostgresDataPath = "",
            BackupPath = "",
            SeqDataPath = "",
            TorrentDownloadPath = "",
            ArchiveTempPath = "",
        };

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.Contains("MINIO_DATA_PATH=\n", env);
        Assert.Contains("POSTGRES_DATA_PATH=\n", env);
        Assert.Contains("BACKUP_PATH=\n", env);
        Assert.Contains("SEQ_DATA_PATH=\n", env);
        Assert.Contains("TORRENT_DOWNLOAD_PATH=\n", env);
        Assert.Contains("ARCHIVE_TEMP_PATH=\n", env);
        Assert.Contains("${MINIO_DATA_PATH:-cloud-minio_data}:/mnt/minio-data:ro", compose);
        Assert.Contains("${MINIO_DATA_PATH:-cloud-minio_data}:/data", compose);
        Assert.Contains("${POSTGRES_DATA_PATH:-pgdata}:/var/lib/postgresql", compose);
        Assert.Contains("${BACKUP_PATH:-backup_volume}:/backup", compose);
        Assert.Contains("${SEQ_DATA_PATH:-seq_data}:/data", compose);
        Assert.Contains("${TORRENT_DOWNLOAD_PATH:-torrent_data}:/mnt/torrents", compose);
        Assert.Contains("${ARCHIVE_TEMP_PATH:-archive_temp}:/mnt/archive-temp", compose);
        Assert.Contains("  cloud-minio_data:\n", compose);
        Assert.Contains("  pgdata:\n", compose);
        Assert.Contains("  backup_volume:\n", compose);
        Assert.Contains("  seq_data:\n", compose);
        Assert.Contains("  torrent_data:\n", compose);
        Assert.Contains("  archive_temp:\n", compose);
    }

    [Fact]
    public void Compose_mode_uses_custom_names_network_and_only_enabled_services()
    {
        var model = new BuilderModel
        {
            NginxLocation = "Compose",
            DockerNetworkName = "barkcloud-prod",
            IdentityContainerName = "prod-identity",
            WebContainerName = "prod-web",
            NginxContainerName = "prod-proxy",
            IncludeNotification = false,
            IncludeTorrent = false,
            IncludeSeq = false,
            NginxSeqProxy = true,
            CertCrtPath = Path.Combine(Path.GetTempPath(), "certs", "public.crt"),
            CertKeyPath = Path.Combine(Path.GetTempPath(), "certs", "private.key"),
        };

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);
        var nginx = BackendComposeGenerator.BuildNginxConf(model);

        Assert.Contains("container_name: ${NGINX_CONTAINER_NAME}", compose);
        Assert.Contains("name: ${DOCKER_NETWORK_NAME}", compose);
        Assert.Contains("DOCKER_NETWORK_NAME=barkcloud-prod", env);
        Assert.Contains("IDENTITY_CONTAINER_NAME=prod-identity", env);
        Assert.Contains("WEB_CONTAINER_NAME=prod-web", env);
        Assert.Contains("NGINX_CONTAINER_NAME=prod-proxy", env);
        Assert.DoesNotContain("NOTIFICATION_CONTAINER_NAME=", env);
        Assert.DoesNotContain("TORRENT_CONTAINER_NAME=", env);
        Assert.DoesNotContain("SEQ_CONTAINER_NAME=", env);
        Assert.DoesNotContain("NGINX_SEQ_PORT=", env);
        Assert.DoesNotContain("Docker__ContainerNames__Notification", compose);
        Assert.DoesNotContain("Docker__ContainerNames__Torrent", compose);
        Assert.DoesNotContain("${TORRENT_PORT}:${TORRENT_PORT}", compose);
        Assert.Contains("\"443:443\"", compose);
        Assert.Contains("\"${IDENTITY_PORT}:${IDENTITY_PORT}\"", compose);
        Assert.Contains("server prod-identity:8000 resolve;", nginx);
        Assert.Contains("server prod-web:8080 resolve;", nginx);
        Assert.Contains("/etc/nginx/certs/public.crt", nginx);
        Assert.Contains("/etc/nginx/certs/private.key", nginx);
        Assert.DoesNotContain("cloud-torrent", nginx);
        Assert.DoesNotContain("barkcloud_seq", nginx);
        Assert.DoesNotContain("{{", compose);
    }

    [Fact]
    public void No_nginx_mode_writes_only_compose_and_env_contracts()
    {
        var model = new BuilderModel { UseNginx = false };

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.DoesNotContain("  cloud-nginx:", BackendComposeGenerator.BuildCompose(model));
        Assert.DoesNotContain("NGINX_CONTAINER_NAME=", BackendComposeGenerator.BuildEnv(model));
    }

    [Fact]
    public void Compose_mode_interpolates_torrent_proxy_ports_and_dependencies()
    {
        var compose = BackendComposeGenerator.BuildCompose(new BuilderModel { NginxLocation = "Compose" });

        Assert.Contains("      - \"${TORRENT_PORT}:${TORRENT_PORT}\"", compose);
        Assert.Contains("      - cloud-torrent", compose);
        Assert.DoesNotContain("{{", compose);
        Assert.DoesNotContain("}}", compose);
    }

    [Fact]
    public void Custom_container_names_and_ports_flow_into_nginx_without_changing_stable_service_dns()
    {
        var model = new BuilderModel
        {
            IdentityContainerName = "identity-prod",
            IdentityPort = "8120",
            ConfigurationContainerName = "config-prod",
            ConfigurationPort = "8123",
            FilesContainerName = "files-prod",
            FilesPort = "8125",
            FilesHttp1Port = "8126",
            TorrentContainerName = "torrent-prod",
            TorrentPort = "8127",
            TorrentHttp1Port = "8128",
        };

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);
        var nginx = BackendComposeGenerator.BuildNginxConf(model);

        Assert.Contains("IDENTITY_CONTAINER_NAME=identity-prod", env);
        Assert.Contains("CONFIGURATION_CONTAINER_NAME=config-prod", env);
        Assert.Contains("CONFIGURATION_SERVICE_URL=http://cloud-configuration:8123", env);
        Assert.Contains("EXTERNAL_IDENTITY_HOST=https://cloud.barkfluff.com:8000", env);
        Assert.Contains("container_name: ${IDENTITY_CONTAINER_NAME}", compose);
        Assert.Contains("server identity-prod:8120 resolve;", nginx);
        Assert.Contains("server files-prod:8125 resolve;", nginx);
        Assert.Contains("server files-prod:8126 resolve;", nginx);
        Assert.Contains("server torrent-prod:8127 resolve;", nginx);
        Assert.Contains("server torrent-prod:8128 resolve;", nginx);
        Assert.Contains("listen 8120 ssl;", nginx);
        Assert.Contains("listen 8127 ssl;", nginx);
    }

    [Fact]
    public void Optional_console_proxies_use_configured_ports_and_fixed_backends()
    {
        var model = new BuilderModel
        {
            NginxLocation = "Compose",
            IncludeSeq = true,
            NginxSeqProxy = true,
            NginxSeqPort = "38882",
            NginxMinioConsoleProxy = true,
            NginxMinioConsolePort = "39022",
            SeqContainerName = "logs-prod",
            MinioContainerName = "objects-prod",
        };

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);
        var nginx = BackendComposeGenerator.BuildNginxConf(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.Contains("NGINX_SEQ_PORT=38882", env);
        Assert.Contains("NGINX_MINIO_CONSOLE_PORT=39022", env);
        Assert.Contains("\"${NGINX_SEQ_PORT}:${NGINX_SEQ_PORT}\"", compose);
        Assert.Contains("\"${NGINX_MINIO_CONSOLE_PORT}:${NGINX_MINIO_CONSOLE_PORT}\"", compose);
        Assert.Contains("listen 38882 ssl;", nginx);
        Assert.Contains("server logs-prod:80 resolve;", nginx);
        Assert.Contains("listen 39022 ssl;", nginx);
        Assert.Contains("server objects-prod:9001 resolve;", nginx);
        Assert.Contains("proxy_set_header Upgrade $http_upgrade;", nginx);
        Assert.Contains("proxy_set_header X-Forwarded-Port $server_port;", nginx);
        Assert.Contains("proxy_read_timeout 300s;", nginx);
        Assert.Contains("proxy_send_timeout 300s;", nginx);
        Assert.Contains("client_max_body_size 0;", nginx);
        Assert.Contains("proxy_request_buffering off;", nginx);
        Assert.Contains("client_body_timeout 7200s;", nginx);
    }

    [Fact]
    public void Container_names_cannot_collide_with_another_service_alias_or_enabled_name()
    {
        var reservedAlias = new BuilderModel
        {
            IdentityContainerName = "custom-identity",
            WebContainerName = "cloud-identity",
            IncludeNotification = false,
        };
        var duplicate = new BuilderModel
        {
            IdentityContainerName = "shared-name",
            WebContainerName = "shared-name",
        };
        var reservedDisabledAlias = new BuilderModel
        {
            IncludeNotification = false,
            WebContainerName = "cloud-notification",
        };

        Assert.Contains(BuilderModelValidator.Validate(reservedAlias), x => x.Contains("зарезервировано именем сервиса"));
        Assert.Contains(BuilderModelValidator.Validate(duplicate), x => x.Contains("используется несколькими сервисами"));
        Assert.Contains(BuilderModelValidator.Validate(reservedDisabledAlias), x => x.Contains("зарезервировано именем сервиса"));
    }

    [Fact]
    public void One_character_container_names_are_rejected_for_core_and_enabled_optional_services()
    {
        var model = new BuilderModel
        {
            WebContainerName = "a",
            NotificationContainerName = "b",
        };

        var errors = BuilderModelValidator.Validate(model);

        Assert.Contains(errors, x => x.Contains("Имя контейнера cloud-web") && x.Contains("не менее двух символов"));
        Assert.Contains(errors, x => x.Contains("Имя контейнера cloud-notification") && x.Contains("не менее двух символов"));
    }

    [Fact]
    public void Two_character_container_names_are_valid_and_single_character_network_names_remain_valid()
    {
        var model = new BuilderModel
        {
            DockerNetworkName = "n",
            WebContainerName = "ab",
            NotificationContainerName = "xy",
        };

        Assert.Empty(BuilderModelValidator.Validate(model));
    }

    [Fact]
    public void Disabled_optional_container_name_is_not_validated()
    {
        var model = new BuilderModel
        {
            IncludeNotification = false,
            NotificationContainerName = "a",
        };

        Assert.Empty(BuilderModelValidator.Validate(model));
    }

    [Fact]
    public void Invalid_network_ports_mode_and_external_paths_are_rejected()
    {
        var model = new BuilderModel
        {
            DockerNetworkName = "contains spaces",
            IdentityPort = "443",
            FilesHttp1Port = "not-a-port",
            TorrentHttp1Port = "65536",
            IncludeSeq = true,
            NginxSeqProxy = true,
            NginxSeqPort = "8005",
            NginxMinioConsoleProxy = true,
            NginxMinioConsolePort = "65536",
            ExternalNginxCertificatePath = "/etc/nginx/certs/bad\";include.conf",
        };

        var errors = BuilderModelValidator.Validate(model);

        Assert.Contains(errors, x => x.Contains("Docker-сети"));
        Assert.Contains(errors, x => x.Contains("Порт 443 указан"));
        Assert.Contains(errors, x => x.Contains("MinIO Console"));
        Assert.Contains(errors, x => x.Contains("Files HTTP/1"));
        Assert.Contains(errors, x => x.Contains("Torrent HTTP/1"));
        Assert.Contains(errors, x => x.Contains("Порт 8005 указан"));
        Assert.Contains(errors, x => x.Contains("Путь к сертификату внешнего nginx"));
    }

    [Theory]
    [InlineData(" 7025")]
    [InlineData("7025 ")]
    [InlineData("+7025")]
    public void Ports_with_whitespace_or_signs_are_rejected(string value)
    {
        var errors = BuilderModelValidator.Validate(new BuilderModel { FilesPort = value });

        Assert.Contains(errors, x => x.Contains("Порт Files должен быть числом"));
    }

    [Fact]
    public void Validation_rejects_trailing_line_feeds_in_names_ports_and_nginx_tokens()
    {
        var errors = BuilderModelValidator.Validate(new BuilderModel
        {
            DockerNetworkName = "barkcloud-network\n",
            WebContainerName = "cloud-web\n",
            IdentityPort = "8000\n",
            NginxLocation = "Compose",
            CertCrtPath = "certificate.pem\n",
        });

        Assert.Contains(errors, x => x.Contains("Docker-сети"));
        Assert.Contains(errors, x => x.Contains("Имя контейнера cloud-web"));
        Assert.Contains(errors, x => x.Contains("Порт Identity должен быть числом"));
        Assert.Contains(errors, x => x.Contains("Файл сертификата") && x.Contains("недопустимые символы"));
    }

    [Fact]
    public void New_defaults_enable_only_console_and_omit_relay_and_helpers()
    {
        var model = new BuilderModel();
        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.False(model.IncludeSeq);
        Assert.False(model.IncludeLognara);
        Assert.Empty(BackendComposeGenerator.BuildLognaraDockerfiles(model));
        Assert.DoesNotContain("lognara-relay", compose);
        Assert.DoesNotContain("LOGNARA_CORE_URL=", env);
        Assert.DoesNotContain("LOGNARA_CONFIGURATION_TOKEN=", env);
        Assert.DoesNotContain("entrypoint:", compose);
        Assert.DoesNotContain("init: true", compose);
        Assert.DoesNotContain("stop_grace_period", compose);

        foreach (var app in model.LognaraApplications)
        {
            Assert.True(app.ConsoleOutput);
            Assert.False(app.SeqEnable);
            Assert.False(app.LognaraEnable);
            Assert.Contains($"{app.Prefix}_CONSOLE_OUTPUT=true", env);
            Assert.Contains($"{app.Prefix}_SEQ_ENABLE=false", env);
            Assert.Contains($"{app.Prefix}_LOGNARA_ENABLE=false", env);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Application_sinks_remain_independent_for_all_eight_combinations(bool console, bool seq, bool lognara)
    {
        var model = ValidLognaraModel();
        foreach (var application in model.LognaraApplications)
        {
            application.ConsoleOutput = console;
            application.SeqEnable = seq;
            application.LognaraEnable = lognara;
        }

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        foreach (var application in model.LognaraApplications)
        {
            Assert.Contains($"ConsoleOutput: ${{{application.Prefix}_CONSOLE_OUTPUT}}", compose);
            Assert.Contains($"SeqEnable: ${{{application.Prefix}_SEQ_ENABLE}}", compose);
            Assert.Contains($"LognaraEnable: ${{{application.Prefix}_LOGNARA_ENABLE}}", compose);
            Assert.Contains($"{application.Prefix}_CONSOLE_OUTPUT={console.ToString().ToLowerInvariant()}", env);
            Assert.Contains($"{application.Prefix}_SEQ_ENABLE={seq.ToString().ToLowerInvariant()}", env);
            Assert.Contains($"{application.Prefix}_LOGNARA_ENABLE={lognara.ToString().ToLowerInvariant()}", env);
            Assert.Equal(lognara, compose.Contains($"LOGNARA_RELAY_TOKEN: ${{{application.TokenVariable}}}"));
        }
    }

    [Fact]
    public void Global_toggles_change_only_their_channel_and_app_overrides_are_kept()
    {
        var model = new BuilderModel();
        var configuration = App(model, "CONFIGURATION");

        model.SetGlobalSeq(true);
        model.SetGlobalLognara(true);
        configuration.ConsoleOutput = false;
        configuration.SeqEnable = false;
        configuration.LognaraEnable = false;
        model.SetGlobalSeq(false);

        Assert.False(configuration.ConsoleOutput);
        Assert.False(configuration.SeqEnable);
        Assert.False(model.LognaraApplications.Single(x => x.Prefix == "IDENTITY").SeqEnable);
        Assert.False(configuration.LognaraEnable);
        Assert.True(model.LognaraApplications.Single(x => x.Prefix == "IDENTITY").LognaraEnable);
    }

    [Fact]
    public void Optional_application_flags_survive_import_while_service_is_absent()
    {
        var model = new BuilderModel { IncludeNotification = false, IncludeTorrent = false };
        model.SetGlobalLognara(true);

        var imported = BuilderConfigLoader.Load(
            BackendComposeGenerator.BuildCompose(model),
            BackendComposeGenerator.BuildEnv(model),
            null,
            "C:\\out");

        Assert.False(imported.IncludeNotification);
        Assert.False(imported.IncludeTorrent);
        Assert.True(App(imported, "NOTIFICATION").LognaraEnable);
        Assert.True(App(imported, "TORRENT").LognaraEnable);
        Assert.DoesNotContain("  cloud-notification:", BackendComposeGenerator.BuildCompose(imported));
        Assert.DoesNotContain("  cloud-torrent:", BackendComposeGenerator.BuildCompose(imported));

        imported.IncludeNotification = true;
        Assert.Contains("LognaraEnable: ${NOTIFICATION_LOGNARA_ENABLE}", BackendComposeGenerator.BuildCompose(imported));
    }

    [Fact]
    public void Lognara_generates_selected_app_agents_relay_and_distinct_spools()
    {
        var model = ValidLognaraModel();
        model.LognaraCoreUrl = "https://logs.example.test/v1/batches";
        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.Contains("image: ghcr.io/liis17/lognara-relay:master", compose);
        Assert.Contains("LOGNARA_LISTEN_ADDR: \"0.0.0.0:7401\"", compose);
        Assert.Contains("LOGNARA_CORE_URL: \"${LOGNARA_CORE_URL}\"", compose);
        Assert.Contains("LOGNARA_CORE_TOKEN: \"${LOGNARA_RELAY_CORE_TOKEN}\"", compose);
        Assert.Contains("LOGNARA_RELAY_MEMORY_MB: \"${LOGNARA_RELAY_MEMORY_MB:-2048}\"", compose);
        Assert.Contains("mem_limit: ${LOGNARA_RELAY_MEMORY_MB:-2048}m", compose);
        Assert.Contains("lognara_relay_spool:/var/lib/lognara-relay/spool", compose);
        Assert.DoesNotContain("ports:", compose[compose.IndexOf("  lognara-relay:", StringComparison.Ordinal)..]);
        Assert.Contains("LOGNARA_CORE_URL=https://logs.example.test/v1/batches", env);
        Assert.Contains("LOGNARA_RELAY_CORE_TOKEN=relay-core-token", env);
        Assert.Contains("LOGNARA_RELAY_MEMORY_MB=2048", env);
        Assert.Contains("LOGNARA_AGENT_VERSION=latest", env);
        Assert.Contains("LOGNARA_SERVICE: ${CONFIGURATION_CONTAINER_NAME}", compose);
        Assert.Contains("LOGNARA_RELAY_URL: \"http://lognara-relay:7401/v1/batches\"", compose);
        Assert.Contains("LOGNARA_RELAY_TOKEN: ${LOGNARA_CONFIGURATION_TOKEN}", compose);
        Assert.Contains("init: true", compose);
        Assert.Contains("stop_grace_period: 75s", compose);

        foreach (var app in model.LognaraApplications)
        {
            Assert.Contains($"{app.TokenVariable}=agent-{app.Prefix.ToLowerInvariant()}", env);
            Assert.Contains($"{app.SpoolVariable}=", env);
            Assert.Contains($"LOGNARA_SERVICE: ${{{app.Prefix}_CONTAINER_NAME}}", compose);
            Assert.Contains($"LOGNARA_RELAY_TOKEN: ${{{app.TokenVariable}}}", compose);
            Assert.Contains($"${{{app.SpoolVariable}:-lognara_{app.Prefix.ToLowerInvariant()}_spool}}:/var/lib/lognara-agent/spool", compose);
            Assert.Contains($"  lognara_{app.Prefix.ToLowerInvariant()}_spool:\n", compose);
        }

        foreach (var service in model.LognaraInfrastructure.Where(model.IsInfrastructureIncluded))
        {
            Assert.Contains($"{service.TokenVariable}=agent-{service.Prefix.ToLowerInvariant()}", env);
            Assert.Contains($"LOGNARA_SERVICE: ${{{service.Prefix}_CONTAINER_NAME}}", compose);
            Assert.Contains($"LOGNARA_RELAY_TOKEN: ${{{service.TokenVariable}}}", compose);
        }
    }

    [Fact]
    public void Disabled_optional_agents_are_not_generated_or_validated()
    {
        var model = ValidLognaraModel();
        model.IncludeNotification = false;
        model.IncludeTorrent = false;
        App(model, "NOTIFICATION").Token = "bad token\n";
        App(model, "NOTIFICATION").SpoolPath = "bad\npath";
        App(model, "TORRENT").Token = "also bad$";

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.DoesNotContain("  cloud-notification:", compose);
        Assert.DoesNotContain("  cloud-torrent:", compose);
        Assert.DoesNotContain("LOGNARA_NOTIFICATION_TOKEN=", env);
        Assert.DoesNotContain("LOGNARA_TORRENT_TOKEN=", env);
        Assert.Contains("NOTIFICATION_LOGNARA_ENABLE=true", env);
        Assert.Contains("TORRENT_LOGNARA_ENABLE=true", env);
    }

    [Fact]
    public void Disabled_optional_infrastructure_agents_are_not_built_or_validated()
    {
        var model = ValidLognaraModel();
        model.IncludeMinio = false;
        model.IncludeRabbitmq = false;
        model.IncludePostgres = false;
        model.SetGlobalSeq(false);
        Infra(model, "MINIO").Token = "bad token";
        Infra(model, "MINIO").SpoolPath = "bad\npath";
        Infra(model, "SEQ").Token = "bad$token";

        var compose = BackendComposeGenerator.BuildCompose(model);
        var env = BackendComposeGenerator.BuildEnv(model);

        Assert.Empty(BuilderModelValidator.Validate(model));
        Assert.DoesNotContain("  cloud-minio:\n", compose);
        Assert.DoesNotContain("  cloud-rabbitmq:\n", compose);
        Assert.DoesNotContain("  cloud-postgres:\n", compose);
        Assert.DoesNotContain("  cloud-seq:\n", compose);
        Assert.DoesNotContain("barkcloud-lognara-minio", compose);
        Assert.DoesNotContain("barkcloud-lognara-postgres", compose);
        Assert.DoesNotContain("LOGNARA_MINIO_TOKEN=", env);
        Assert.DoesNotContain("LOGNARA_SEQ_TOKEN=", env);
        Assert.DoesNotContain("  lognara_minio_spool:", compose);
        Assert.DoesNotContain("  lognara_seq_spool:", compose);
    }

    [Fact]
    public void Optional_bind_spools_replace_only_their_own_named_volumes()
    {
        var model = ValidLognaraModel();
        App(model, "CONFIGURATION").SpoolPath = "./data/config-agent";
        Infra(model, "MINIO").SpoolPath = "./data/minio-agent";

        var compose = BackendComposeGenerator.BuildCompose(model);

        Assert.Contains("${LOGNARA_CONFIGURATION_SPOOL_PATH:-lognara_configuration_spool}:/var/lib/lognara-agent/spool", compose);
        Assert.Contains("${LOGNARA_MINIO_SPOOL_PATH:-lognara_minio_spool}:/var/lib/lognara-agent/spool", compose);
        Assert.DoesNotContain("  lognara_configuration_spool:\n", compose);
        Assert.DoesNotContain("  lognara_minio_spool:\n", compose);
        Assert.Contains("  lognara_identity_spool:\n", compose);
        Assert.Contains("  lognara_relay_spool:\n", compose);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token$part")]
    [InlineData("token#part")]
    [InlineData("token with space")]
    public void Lognara_rejects_missing_or_invalid_active_agent_tokens(string token)
    {
        var model = ValidLognaraModel();
        App(model, "CONFIGURATION").Token = token;

        Assert.Contains(BuilderModelValidator.Validate(model), x => x.Contains("Токен Lognara для Configuration"));
    }

    [Theory]
    [InlineData("/v1/batches")]
    [InlineData("ftp://core.example.test/v1/batches")]
    [InlineData("https://core.example.test/v1/logs")]
    [InlineData("https://core.example.test/v1/batches?x=1")]
    [InlineData("https://core.example.test/v1/batches/")]
    [InlineData("https://user@core.example.test/v1/batches")]
    [InlineData("https://core.example.test/v1/batches with space")]
    public void Lognara_requires_http_or_https_core_batches_endpoint(string url)
    {
        var model = ValidLognaraModel();
        model.LognaraCoreUrl = url;

        Assert.Contains(BuilderModelValidator.Validate(model), x => x.Contains("URL Lognara Core"));
    }

    [Fact]
    public void Lognara_requires_a_unique_token_for_each_active_agent()
    {
        var model = ValidLognaraModel();
        App(model, "IDENTITY").Token = App(model, "CONFIGURATION").Token;

        Assert.Contains(BuilderModelValidator.Validate(model), x => x.Contains("Токен Lognara используется несколькими агентами"));
    }

    [Fact]
    public void Relay_container_name_cannot_collide_with_a_BarkCloud_container()
    {
        var model = ValidLognaraModel();
        model.IdentityContainerName = "lognara-relay";

        Assert.Contains(BuilderModelValidator.Validate(model), x => x.Contains("используется несколькими сервисами"));
    }

    [Fact]
    public void Infrastructure_agent_wrappers_preserve_base_entrypoints_commands_users_and_signals()
    {
        var model = ValidLognaraModel();
        model.NginxLocation = "Compose";
        var files = BackendComposeGenerator.BuildLognaraDockerfiles(model);
        var compose = BackendComposeGenerator.BuildCompose(model);

        Assert.Equal(5, files.Count);
        Assert.Contains("FROM nginx:latest", files["nginx.Dockerfile"]);
        Assert.Contains("ENTRYPOINT [\"/usr/local/bin/lognara-agent\", \"run\", \"--\", \"/docker-entrypoint.sh\"]", files["nginx.Dockerfile"]);
        Assert.Contains("FROM postgres:18", files["postgres.Dockerfile"]);
        Assert.Contains("\"docker-entrypoint.sh\"]", files["postgres.Dockerfile"]);
        Assert.Contains("FROM rabbitmq:latest", files["rabbitmq.Dockerfile"]);
        Assert.Contains("FROM docker.barkfluff.com/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1", files["minio.Dockerfile"]);
        Assert.Contains("\"/usr/bin/docker-entrypoint.sh\"]", files["minio.Dockerfile"]);
        Assert.Contains("FROM datalust/seq:latest", files["seq.Dockerfile"]);
        Assert.Contains("\"/bin/seqentry\"]", files["seq.Dockerfile"]);

        foreach (var dockerfile in files.Values)
        {
            Assert.Contains("ARG TARGETARCH", dockerfile);
            Assert.Contains("amd64|arm64", dockerfile);
            Assert.Contains("SHA256SUMS", dockerfile);
            Assert.Contains("ARG LOGNARA_AGENT_VERSION=latest", dockerfile);
            Assert.DoesNotContain("\nCMD ", dockerfile);
            Assert.DoesNotContain("\nUSER ", dockerfile);
            Assert.DoesNotContain("\nSTOPSIGNAL ", dockerfile);
        }

        Assert.Contains("stop_grace_period: 30s", compose);
        Assert.Contains("logging_collector=off", compose);
        Assert.Contains("log_destination=stderr", compose);
        Assert.Contains("command: server /data --console-address \":9001\"", compose);
        Assert.DoesNotContain("ports:", compose[compose.IndexOf("  lognara-relay:", StringComparison.Ordinal)..]);
    }

    private static BuilderModel ValidLognaraModel()
    {
        var model = new BuilderModel
        {
            IncludeSeq = true,
            LognaraCoreUrl = "https://core.example.test/v1/batches",
            LognaraCoreToken = "relay-core-token",
            LognaraServer = "node-1",
            LognaraBackend = "barkcloud",
            LognaraEnvironment = "Production",
        };
        model.SetGlobalSeq(true);
        model.SetGlobalLognara(true);

        foreach (var application in model.LognaraApplications)
            application.Token = $"agent-{application.Prefix.ToLowerInvariant()}";
        foreach (var service in model.LognaraInfrastructure)
            service.Token = $"agent-{service.Prefix.ToLowerInvariant()}";

        return model;
    }

    private static LognaraApplicationSettings App(BuilderModel model, string prefix) =>
        model.LognaraApplications.Single(x => x.Prefix == prefix);

    private static LognaraInfrastructureSettings Infra(BuilderModel model, string prefix) =>
        model.LognaraInfrastructure.Single(x => x.Prefix == prefix);
}
