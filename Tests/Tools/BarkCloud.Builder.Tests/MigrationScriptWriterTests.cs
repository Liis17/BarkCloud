using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class MigrationScriptWriterTests
{
    [Fact]
    public void WriteTo_CopiesMigrationAndIsolationScriptsIntoDeploymentOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "barkcloud-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MigrationScriptWriter.WriteTo(directory);

            foreach (var (resourceName, fileName) in new[]
                     {
                         ("BarkCloud.Builder.migrate-secrets.sh", "migrate-secrets.sh"),
                         ("BarkCloud.Builder.provision-isolation.sh", "provision-isolation.sh"),
                         ("BarkCloud.Builder.provision-postgres.sql", "provision-postgres.sql"),
                         ("BarkCloud.Builder.provision-postgres-ownership.sql", "provision-postgres-ownership.sql"),
                         ("BarkCloud.Builder.provision-rabbitmq.sh", "provision-rabbitmq.sh"),
                         ("BarkCloud.Builder.postgres-isolation-smoke.sh", "postgres-isolation-smoke.sh"),
                         ("BarkCloud.Builder.rabbitmq-isolation-smoke.sh", "rabbitmq-isolation-smoke.sh"),
                         ("BarkCloud.Builder.isolation-guide.md", "isolation-guide.md")
                     })
            {
                using var resource = typeof(MigrationScriptWriter).Assembly.GetManifestResourceStream(resourceName);
                Assert.NotNull(resource);
                using var expected = new MemoryStream();
                resource!.CopyTo(expected);
                Assert.Equal(expected.ToArray(), File.ReadAllBytes(Path.Combine(directory, fileName)));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
