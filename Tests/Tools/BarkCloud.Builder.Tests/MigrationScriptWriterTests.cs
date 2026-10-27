using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class MigrationScriptWriterTests
{
    [Fact]
    public void WriteTo_CopiesMigrationScriptIntoDeploymentOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "barkcloud-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MigrationScriptWriter.WriteTo(directory);

            using var resource = typeof(MigrationScriptWriter).Assembly
                .GetManifestResourceStream(MigrationScriptWriter.ResourceName);
            Assert.NotNull(resource);
            using var expected = new MemoryStream();
            resource!.CopyTo(expected);
            Assert.Equal(expected.ToArray(), File.ReadAllBytes(Path.Combine(directory, "migrate-secrets.sh")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
