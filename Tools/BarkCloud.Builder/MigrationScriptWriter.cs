using System.IO;

namespace BarkCloud.Builder;

internal static class MigrationScriptWriter
{
    internal const string ResourceName = "BarkCloud.Builder.migrate-secrets.sh";

    internal static void WriteTo(string directory)
    {
        using var script = typeof(MigrationScriptWriter).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("В сборке Builder не найден migrate-secrets.sh.");
        using var output = File.Create(Path.Combine(directory, "migrate-secrets.sh"));
        script.CopyTo(output);
    }
}
