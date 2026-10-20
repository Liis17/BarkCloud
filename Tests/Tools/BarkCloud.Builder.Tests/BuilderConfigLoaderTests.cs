using System.IO;
using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class BuilderConfigLoaderTests
{
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
            ConfigurationAccessKey = "key=with=equals",
            WebCookieSecure = true,
            WebAdminPassword = "admin",
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
