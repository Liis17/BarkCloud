using BarkCloud.Files.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class PhysicalStorageStatsProviderTests
{
    [Fact]
    public async Task GetStatsAsync_CachesDirectorySize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"barkcloud-storage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "first.bin"), new byte[3]);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageProbe:Path"] = root
                })
                .Build();
            var sut = new PhysicalStorageStatsProvider(
                configuration,
                NullLogger<PhysicalStorageStatsProvider>.Instance);

            var first = await sut.GetStatsAsync();

            await File.WriteAllBytesAsync(Path.Combine(root, "second.bin"), new byte[5]);
            var second = await sut.GetStatsAsync();

            first.S3UsedBytes.Should().Be(3);
            second.S3UsedBytes.Should().Be(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
