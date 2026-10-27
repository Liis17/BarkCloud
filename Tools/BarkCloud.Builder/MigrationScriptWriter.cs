using System.IO;
using System.Linq;

namespace BarkCloud.Builder;

internal static class MigrationScriptWriter
{
    internal const string ResourceName = "BarkCloud.Builder.migrate-secrets.sh";
    private static readonly (string ResourceName, string FileName)[] DeploymentArtifacts =
    [
        ("BarkCloud.Builder.provision-isolation.sh", "provision-isolation.sh"),
        ("BarkCloud.Builder.provision-postgres.sql", "provision-postgres.sql"),
        ("BarkCloud.Builder.provision-postgres-ownership.sql", "provision-postgres-ownership.sql"),
        ("BarkCloud.Builder.provision-rabbitmq.sh", "provision-rabbitmq.sh"),
        ("BarkCloud.Builder.postgres-isolation-smoke.sh", "postgres-isolation-smoke.sh"),
        ("BarkCloud.Builder.rabbitmq-isolation-smoke.sh", "rabbitmq-isolation-smoke.sh"),
        ("BarkCloud.Builder.isolation-guide.md", "isolation-guide.md")
    ];

    internal static void WriteTo(string directory)
    {
        var assembly = typeof(MigrationScriptWriter).Assembly;
        foreach (var (resourceName, fileName) in new[] { (ResourceName, "migrate-secrets.sh") }.Concat(DeploymentArtifacts))
        {
            using var script = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException($"В сборке Builder не найден {fileName}.");
            using var output = File.Create(Path.Combine(directory, fileName));
            script.CopyTo(output);
        }
    }
}
