using System.IO;
using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class BuilderConfigLoaderTests
{
    [Fact]
    public void Video_transcoder_settings_round_trip_and_replace_placeholder_token()
    {
        var model = new BuilderModel
        {
            TranscodeWorkPath = "./video-work",
            TranscoderToken = "video-$-transcoder-secret",
            TranscoderThreads = "4",
            TranscoderCpus = "2.5",
            TranscoderMemory = "6g",
        };

        var loaded = RoundTrip(model, "C:\\out");

        Assert.Equal(model.TranscodeWorkPath, loaded.TranscodeWorkPath);
        Assert.Equal(model.TranscoderToken, loaded.TranscoderToken);
        Assert.Equal(model.TranscoderThreads, loaded.TranscoderThreads);
        Assert.Equal(model.TranscoderCpus, loaded.TranscoderCpus);
        Assert.Equal(model.TranscoderMemory, loaded.TranscoderMemory);
        AssertSameOutput(model, loaded);

        var defaults = new BuilderModel();
        var repaired = BuilderConfigLoader.Load(
            BackendComposeGenerator.BuildCompose(defaults),
            "TRANSCODER_TOKEN=replace-transcoder-token\n",
            null,
            "C:\\out");

        Assert.False(string.IsNullOrWhiteSpace(repaired.TranscoderToken));
        Assert.NotEqual("replace-transcoder-token", repaired.TranscoderToken);
    }

    [Fact]
    public void Registry_only_env_restores_split_secrets_and_prefers_explicit_values()
    {
        const string env = """
IDENTITY_CONFIG_ACCESS_KEY="replace-identity-key" # old placeholder
USERS_CONFIG_ACCESS_KEY="custom-users-key"
CONFIGURATION_ACCESS_KEYS='Identity=registry-identity-key;Users=registry-users-key;Notification=notification-key;Files=files-key;Torrent=torrent-key;Web=web-key'
SERVICE_CREDENTIALS='Identity=identity-credential;Users=users-credential;Files=files-credential;Torrent=torrent-credential;Web=web-credential'
""";

        var model = new BuilderModel();
        var loaded = BuilderConfigLoader.Load(BackendComposeGenerator.BuildCompose(model), env, null, "C:\\out");
        var generatedEnv = BackendComposeGenerator.BuildEnv(loaded);

        Assert.Equal("registry-identity-key", loaded.IdentityConfigAccessKey);
        Assert.Equal("custom-users-key", loaded.UsersConfigAccessKey);
        Assert.Equal("notification-key", loaded.NotificationConfigAccessKey);
        Assert.Equal("files-key", loaded.FilesConfigAccessKey);
        Assert.Equal("torrent-key", loaded.TorrentConfigAccessKey);
        Assert.Equal("web-key", loaded.WebConfigAccessKey);
        Assert.Equal("identity-credential", loaded.IdentityServiceCredential);
        Assert.Equal("users-credential", loaded.UsersServiceCredential);
        Assert.Equal("files-credential", loaded.FilesServiceCredential);
        Assert.Equal("torrent-credential", loaded.TorrentServiceCredential);
        Assert.Equal("web-credential", loaded.WebServiceCredential);
        Assert.Contains("IDENTITY_CONFIG_ACCESS_KEY='registry-identity-key'\n", generatedEnv);
        Assert.Contains("USERS_CONFIG_ACCESS_KEY='custom-users-key'\n", generatedEnv);
    }

    [Fact]
    public void Service_database_and_rabbit_passwords_round_trip_and_legacy_placeholders_are_replaced()
    {
        var model = new BuilderModel
        {
            ConfigurationDbPassword = "configuration-db-secret",
            IdentityDbPassword = "identity-db-secret",
            UsersDbPassword = "users-db-secret",
            FilesDbPassword = "files-db-secret",
            TorrentDbPassword = "torrent-db-secret",
            IdentityRabbitPassword = "identity'quoted;secret",
            UsersRabbitPassword = "users-rabbit-secret",
            FilesRabbitPassword = "files-rabbit-secret",
            TorrentRabbitPassword = "torrent-rabbit-secret",
            NotificationRabbitPassword = "notification-rabbit-secret",
            PostgresHost = "postgres.example.test",
            RabbitHost = "rabbit.example.test",
            RabbitVirtualHost = "/barkcloud"
        };

        var loaded = RoundTrip(model, "C:\\out");
        Assert.Equal(model.ConfigurationDbPassword, loaded.ConfigurationDbPassword);
        Assert.Equal(model.IdentityDbPassword, loaded.IdentityDbPassword);
        Assert.Equal(model.UsersDbPassword, loaded.UsersDbPassword);
        Assert.Equal(model.FilesDbPassword, loaded.FilesDbPassword);
        Assert.Equal(model.TorrentDbPassword, loaded.TorrentDbPassword);
        Assert.Equal(model.IdentityRabbitPassword, loaded.IdentityRabbitPassword);
        Assert.Equal(model.UsersRabbitPassword, loaded.UsersRabbitPassword);
        Assert.Equal(model.FilesRabbitPassword, loaded.FilesRabbitPassword);
        Assert.Equal(model.TorrentRabbitPassword, loaded.TorrentRabbitPassword);
        Assert.Equal(model.NotificationRabbitPassword, loaded.NotificationRabbitPassword);
        Assert.Equal(model.PostgresHost, loaded.PostgresHost);
        Assert.Equal(model.RabbitHost, loaded.RabbitHost);
        Assert.Equal(model.RabbitVirtualHost, loaded.RabbitVirtualHost);
        var env = BackendComposeGenerator.BuildEnv(loaded);
        Assert.Contains("POSTGRES_HOST='postgres.example.test'\n", env);
        Assert.Contains("RABBITMQ_HOST='rabbit.example.test'\n", env);
        Assert.Contains("RABBITMQ_VIRTUAL_HOST='/barkcloud'\n", env);
        AssertSameOutput(model, loaded);

        const string legacyEnv = "CONFIGURATION_DB_PASSWORD=replace-configuration-db-password\n"
                                 + "IDENTITY_DB_PASSWORD=replace-identity-db-password\n"
                                 + "RABBITMQ_TORRENT_PASSWORD=replace-torrent-rabbitmq-password\n";
        var repaired = BuilderConfigLoader.Load(
            BackendComposeGenerator.BuildCompose(new BuilderModel()), legacyEnv, null, "C:\\out");
        Assert.False(repaired.ConfigurationDbPassword.StartsWith("replace-", StringComparison.Ordinal));
        Assert.False(repaired.IdentityDbPassword.StartsWith("replace-", StringComparison.Ordinal));
        Assert.False(repaired.TorrentRabbitPassword.StartsWith("replace-", StringComparison.Ordinal));
        Assert.NotEqual(string.Empty, repaired.ConfigurationDbPassword);
        Assert.Equal(BackendComposeGenerator.BuildEnv(repaired), BackendComposeGenerator.BuildEnv(repaired));

        var reopened = RoundTrip(repaired, "C:\\out");
        Assert.Equal(repaired.ConfigurationDbPassword, reopened.ConfigurationDbPassword);
        Assert.Equal(repaired.IdentityDbPassword, reopened.IdentityDbPassword);
        Assert.Equal(repaired.TorrentRabbitPassword, reopened.TorrentRabbitPassword);
    }

    [Fact]
    public void External_mode_round_trip_restores_all_generated_files()
    {
        var model = new BuilderModel
        {
            ImageChannel = "Dev",
            IncludeTorrent = false,
            IncludeRabbitmq = false,
            IncludeSeq = true,
            IncludeLognara = true,
            LognaraCoreUrl = "https://core.example.test/v1/batches",
            LognaraCoreToken = "relay-core-token",
            LognaraServer = "srv",
            LognaraEnvironment = "prod",
            LognaraAgentVersion = "agent-20261018-120000-123456abcdef",
            DockerNetworkName = "custom-net",
            WebContainerName = "my-web",
            IdentityConfigAccessKey = "identity-key=with=equals",
            UsersConfigAccessKey = "users-key",
            NotificationConfigAccessKey = "notification-key",
            FilesConfigAccessKey = "files-key",
            TorrentConfigAccessKey = "torrent-key",
            WebConfigAccessKey = "web-key",
            IdentityServiceCredential = "identity-credential",
            UsersServiceCredential = "users-credential",
            FilesServiceCredential = "files-credential",
            TorrentServiceCredential = "torrent-credential",
            WebServiceCredential = "web-credential",
            WebCookieSecure = true,
            WebAdminPassword = "admin",
            AndroidCertificateSha256Fingerprint = "AA:BB:CC:DD",
            IdentityPort = "9100",
            ArchiveTempPath = "",
            NginxSeqProxy = true,
            NginxSeqPort = "40000",
            NginxDomain = "example.org",
            ExternalNginxCertificatePath = "/certs/a.pem",
            ExternalNginxKeyPath = "/certs/a.key",
            EmailHost = "smtp.example.org",
            EmailPort = "587",
            EmailSenderEmail = "a@example.org",
            EmailSenderPassword = "pw",
        };
        model.SetGlobalLognara(true);
        foreach (var app in model.LognaraApplications)
        {
            app.Token = $"agent-{app.Prefix.ToLowerInvariant()}";
            app.SpoolPath = $"./data/{app.Prefix.ToLowerInvariant()}-spool";
        }
        foreach (var service in model.LognaraInfrastructure)
        {
            service.Token = $"agent-{service.Prefix.ToLowerInvariant()}";
            service.SpoolPath = $"./data/{service.Prefix.ToLowerInvariant()}-spool";
        }

        var loaded = RoundTrip(model, "C:\\out");

        Assert.Equal("C:\\out", loaded.OutputPath);
        Assert.Equal("External", loaded.NginxLocation);
        Assert.True(loaded.UseNginx);
        Assert.True(loaded.NginxSeqProxy);
        Assert.False(loaded.NginxMinioConsoleProxy);
        Assert.Equal(model.IdentityConfigAccessKey, loaded.IdentityConfigAccessKey);
        Assert.Equal(model.UsersConfigAccessKey, loaded.UsersConfigAccessKey);
        Assert.Equal(model.NotificationConfigAccessKey, loaded.NotificationConfigAccessKey);
        Assert.Equal(model.FilesConfigAccessKey, loaded.FilesConfigAccessKey);
        Assert.Equal(model.TorrentConfigAccessKey, loaded.TorrentConfigAccessKey);
        Assert.Equal(model.WebConfigAccessKey, loaded.WebConfigAccessKey);
        Assert.Equal(model.IdentityServiceCredential, loaded.IdentityServiceCredential);
        Assert.Equal(model.UsersServiceCredential, loaded.UsersServiceCredential);
        Assert.Equal(model.FilesServiceCredential, loaded.FilesServiceCredential);
        Assert.Equal(model.TorrentServiceCredential, loaded.TorrentServiceCredential);
        Assert.Equal(model.WebServiceCredential, loaded.WebServiceCredential);
        AssertSameOutput(model, loaded);
    }

    [Fact]
    public void Logging_flags_and_active_agent_settings_round_trip()
    {
        var model = new BuilderModel
        {
            IncludeSeq = true,
            IncludeLognara = true,
            LognaraCoreUrl = "https://core.example.test/v1/batches",
            LognaraCoreToken = "relay-core-token",
            LognaraServer = "srv",
            LognaraBackend = "barkcloud",
            LognaraEnvironment = "prod",
            LognaraAgentVersion = "agent-20261018-120000-123456abcdef",
        };
        model.SetGlobalSeq(true);
        model.SetGlobalLognara(true);

        for (var index = 0; index < model.LognaraApplications.Count; index++)
        {
            var application = model.LognaraApplications[index];
            application.ConsoleOutput = index % 2 == 0;
            application.SeqEnable = index % 3 != 1;
            application.LognaraEnable = index % 2 == 0;
            application.Token = $"agent-{application.Prefix.ToLowerInvariant()}";
            application.SpoolPath = $"./data/{application.Prefix.ToLowerInvariant()}-spool";
        }

        foreach (var service in model.LognaraInfrastructure)
        {
            service.Token = $"agent-{service.Prefix.ToLowerInvariant()}";
            service.SpoolPath = $"./data/{service.Prefix.ToLowerInvariant()}-spool";
        }

        var loaded = RoundTrip(model, "C:\\out");

        Assert.Equal(model.LognaraCoreUrl, loaded.LognaraCoreUrl);
        Assert.Equal(model.LognaraCoreToken, loaded.LognaraCoreToken);
        Assert.Equal(model.LognaraServer, loaded.LognaraServer);
        Assert.Equal(model.LognaraBackend, loaded.LognaraBackend);
        Assert.Equal(model.LognaraEnvironment, loaded.LognaraEnvironment);
        Assert.Equal(model.LognaraAgentVersion, loaded.LognaraAgentVersion);

        foreach (var expected in model.LognaraApplications)
        {
            var actual = loaded.LognaraApplications.Single(x => x.Prefix == expected.Prefix);
            Assert.Equal(expected.ConsoleOutput, actual.ConsoleOutput);
            Assert.Equal(expected.SeqEnable, actual.SeqEnable);
            Assert.Equal(expected.LognaraEnable, actual.LognaraEnable);
            if (model.IsApplicationIncluded(expected) && expected.LognaraEnable)
            {
                Assert.Equal(expected.Token, actual.Token);
                Assert.Equal(expected.SpoolPath, actual.SpoolPath);
            }
        }

        foreach (var expected in model.LognaraInfrastructure.Where(model.IsInfrastructureIncluded))
        {
            var actual = loaded.LognaraInfrastructure.Single(x => x.Prefix == expected.Prefix);
            Assert.Equal(expected.Token, actual.Token);
            Assert.Equal(expected.SpoolPath, actual.SpoolPath);
        }

        AssertSameOutput(model, loaded);
    }

    [Fact]
    public void Compose_mode_restores_local_certificates_from_certs_folder()
    {
        var dir = Directory.CreateTempSubdirectory("builder-loader-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "certs"));
            var crt = Path.Combine(dir, "certs", "site-crt.pem");
            File.WriteAllText(crt, "crt");
            var model = new BuilderModel
            {
                NginxLocation = "Compose",
                ImageChannel = "Dev",
                NginxMinioConsoleProxy = true,
                CertCrtPath = "D:\\src\\site-crt.pem",
                CertKeyPath = "D:\\src\\missing-key.pem",
            };

            var loaded = RoundTrip(model, dir);

            Assert.Equal("Compose", loaded.NginxLocation);
            Assert.Equal("Dev", loaded.ImageChannel);
            Assert.True(loaded.NginxMinioConsoleProxy);
            Assert.Equal(crt, loaded.CertCrtPath);
            Assert.Equal("", loaded.CertKeyPath);
            Assert.Equal(BackendComposeGenerator.BuildCompose(model), BackendComposeGenerator.BuildCompose(loaded));
            Assert.Equal(BackendComposeGenerator.BuildEnv(model), BackendComposeGenerator.BuildEnv(loaded));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_nginx_conf_without_nginx_service_disables_nginx()
    {
        var model = new BuilderModel { UseNginx = false, IncludeSeq = false, IncludeNotification = false };

        var loaded = BuilderConfigLoader.Load(
            BackendComposeGenerator.BuildCompose(model), BackendComposeGenerator.BuildEnv(model), null, "C:\\out");

        Assert.False(loaded.UseNginx);
        AssertSameOutput(model, loaded);
    }

    [Fact]
    public void Legacy_import_keeps_configuration_agent_but_does_not_reuse_external_relay_as_core()
    {
        const string legacyCompose = """
services:
  cloud-configuration:
    image: old-configuration
  cloud-seq:
    image: datalust/seq:latest
""";
        const string legacyEnv = """
LOGNARA_RELAY_URL=http://old-relay:7401/v1/batches
LOGNARA_CONFIGURATION_TOKEN=old-config-token
LOGNARA_CONFIGURATION_SPOOL_PATH=./data/old-config-spool
LOGNARA_SERVER=legacy-node
LOGNARA_BACKEND=barkcloud
LOGNARA_ENVIRONMENT=Production
""";

        var loaded = BuilderConfigLoader.Load(legacyCompose, legacyEnv, null, "C:\\legacy");
        var configuration = loaded.LognaraApplications.Single(x => x.Prefix == "CONFIGURATION");
        var identity = loaded.LognaraApplications.Single(x => x.Prefix == "IDENTITY");

        Assert.True(loaded.LegacyLognaraImport);
        Assert.True(loaded.IncludeLognara);
        Assert.True(loaded.IncludeSeq);
        Assert.False(configuration.ConsoleOutput);
        Assert.True(configuration.SeqEnable);
        Assert.True(configuration.LognaraEnable);
        Assert.Equal("old-config-token", configuration.Token);
        Assert.Equal("./data/old-config-spool", configuration.SpoolPath);
        Assert.True(identity.ConsoleOutput);
        Assert.True(identity.SeqEnable);
        Assert.False(identity.LognaraEnable);
        Assert.Equal("", loaded.LognaraCoreUrl);
        Assert.Equal("", loaded.LognaraCoreToken);
        Assert.DoesNotContain("LOGNARA_RELAY_URL=", BackendComposeGenerator.BuildEnv(loaded));
        Assert.Contains(BuilderModelValidator.Validate(loaded), x => x.Contains("URL Lognara Core"));
        Assert.Contains(BuilderModelValidator.Validate(loaded), x => x.Contains("Токен relay для Lognara Core"));
    }

    [Fact]
    public void Legacy_disabled_lognara_comments_do_not_enable_agents_on_import()
    {
        const string legacyCompose = """
services:
  cloud-configuration:
    image: old-configuration
    # Lognara__AgentUrl: "http://127.0.0.1:7400/v1/logs"
    # entrypoint: ["/usr/local/bin/lognara-agent", "run", "--", "dotnet", "BarkCloud.Configuration.dll"]
""";
        const string legacyEnv = "# LOGNARA_RELAY_URL=http://old-relay:7401/v1/batches\n";

        var loaded = BuilderConfigLoader.Load(legacyCompose, legacyEnv, null, "C:\\legacy");
        var configuration = loaded.LognaraApplications.Single(x => x.Prefix == "CONFIGURATION");

        Assert.False(loaded.IncludeLognara);
        Assert.True(configuration.ConsoleOutput);
        Assert.False(configuration.LognaraEnable);
    }

    [Fact]
    public void Legacy_seq_true_without_seq_service_is_not_silently_preserved_as_a_working_backend()
    {
        const string legacyCompose = "services:\n  cloud-configuration:\n    image: old\n";
        var loaded = BuilderConfigLoader.Load(legacyCompose, "", null, "C:\\legacy");

        Assert.True(loaded.LegacySeqConfigurationUnknown);
        Assert.False(loaded.IncludeSeq);
        Assert.All(loaded.LognaraApplications, app => Assert.False(app.SeqEnable));
    }

    [Fact]
    public void Explicit_seq_flag_with_missing_service_requires_a_visible_resolution()
    {
        const string legacyCompose = "services:\n  cloud-configuration:\n    image: old\n";
        const string env = "CONFIGURATION_SEQ_ENABLE=true\n";
        var loaded = BuilderConfigLoader.Load(legacyCompose, env, null, "C:\\legacy");

        Assert.False(loaded.IncludeSeq);
        Assert.True(loaded.LognaraApplications.Single(x => x.Prefix == "CONFIGURATION").SeqEnable);
        Assert.Contains(BuilderModelValidator.Validate(loaded), x => x.Contains("Seq отключён"));
    }

    private static BuilderModel RoundTrip(BuilderModel model, string dir) =>
        BuilderConfigLoader.Load(
            BackendComposeGenerator.BuildCompose(model),
            BackendComposeGenerator.BuildEnv(model),
            model.UseNginx ? BackendComposeGenerator.BuildNginxConf(model) : null,
            dir);

    private static void AssertSameOutput(BuilderModel expected, BuilderModel actual)
    {
        Assert.Equal(BackendComposeGenerator.BuildCompose(expected), BackendComposeGenerator.BuildCompose(actual));
        Assert.Equal(BackendComposeGenerator.BuildEnv(expected), BackendComposeGenerator.BuildEnv(actual));
        if (expected.UseNginx)
            Assert.Equal(BackendComposeGenerator.BuildNginxConf(expected), BackendComposeGenerator.BuildNginxConf(actual));
    }
}
