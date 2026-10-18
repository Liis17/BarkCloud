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
            ImageChannel = "Nightly",
            IncludeTorrent = false,
            IncludeRabbitmq = false,
            IncludeLognara = true,
            LognaraConfigurationToken = "token",
            LognaraServer = "srv",
            LognaraEnvironment = "prod",
            LognaraConfigurationSpoolPath = "./data/spool",
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

        var loaded = RoundTrip(model, "C:\\out");

        Assert.Equal("C:\\out", loaded.OutputPath);
        Assert.Equal("External", loaded.NginxLocation);
        Assert.True(loaded.UseNginx);
        Assert.True(loaded.NginxSeqProxy);
        Assert.False(loaded.NginxMinioConsoleProxy);
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
