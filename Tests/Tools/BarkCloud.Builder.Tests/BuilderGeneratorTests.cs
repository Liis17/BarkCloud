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
        Assert.Contains("server cloud-web:8080 resolve;", nginx);
        Assert.Contains("server cloud-identity:7020 resolve;", nginx);
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
        Assert.DoesNotContain("listen 80", nginx);
        Assert.DoesNotContain("{{", nginx);
        Assert.DoesNotContain("}}", nginx);
        Assert.DoesNotContain("NGINX_SEQ_PORT", env);
        Assert.DoesNotContain("NGINX_MINIO_CONSOLE_PORT", env);
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
        Assert.Contains("server prod-identity:7020 resolve;", nginx);
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
    public void Optional_console_proxies_use_configured_ports_and_fixed_backends()
    {
        var model = new BuilderModel
        {
            NginxLocation = "Compose",
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
            NginxSeqProxy = true,
            NginxSeqPort = "7025",
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
        Assert.Contains(errors, x => x.Contains("Порт 7025 указан"));
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
            IdentityPort = "7020\n",
            NginxLocation = "Compose",
            CertCrtPath = "certificate.pem\n",
        });

        Assert.Contains(errors, x => x.Contains("Docker-сети"));
        Assert.Contains(errors, x => x.Contains("Имя контейнера cloud-web"));
        Assert.Contains(errors, x => x.Contains("Порт Identity должен быть числом"));
        Assert.Contains(errors, x => x.Contains("Файл сертификата") && x.Contains("недопустимые символы"));
    }
}
