using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class ComposeImageServiceTests
{
    private const string Compose = """
version: '3.8'

services:
  cloud-configuration:
    image: docker.barkfluff.com/barkcloud-configuration-nightly:1.0.7
    container_name: cloud-configuration

  cloud-web:
    image: docker.barkfluff.com/barkcloud-web-dev:latest
    container_name: cloud-web

  cloud-seq:
    image: datalust/seq:latest
""";

    [Fact]
    public void Parse_ReturnsOnlyBarkCloudApplicationImages()
    {
        var images = ComposeImageService.Parse(Compose);

        images.Keys.Should().BeEquivalentTo("cloud-configuration", "cloud-web");
        images["cloud-configuration"].BaseRepository.Should().Be("barkcloud-configuration");
        images["cloud-configuration"].Branch.Should().Be("nightly");
        images["cloud-configuration"].Tag.Should().Be("1.0.7");
        images["cloud-web"].Branch.Should().Be("dev");
    }

    [Theory]
    [InlineData("master", "docker.barkfluff.com/barkcloud-configuration:1.0.7")]
    [InlineData("nightly", "docker.barkfluff.com/barkcloud-configuration-nightly:1.0.7")]
    [InlineData("dev", "docker.barkfluff.com/barkcloud-configuration-dev:1.0.7")]
    public void TryRewrite_SwitchesOnlyRepositorySuffixAndPreservesTag(string branch, string expectedImage)
    {
        ComposeImageService.TryRewrite(Compose, "cloud-configuration", branch, out var result, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        result.Should().Contain($"image: {expectedImage}");
        result.Should().Contain("cloud-web-dev:latest");
    }

    [Fact]
    public void TryRewrite_PreservesCrlfAndTrailingContent()
    {
        var crlf = Compose.Replace("\n", "\r\n");

        ComposeImageService.TryRewrite(crlf, "cloud-web", "master", out var result, out _).Should().BeTrue();

        result.Should().Contain("image: docker.barkfluff.com/barkcloud-web:latest\r\n");
        result.Count(c => c == '\n').Should().Be(crlf.Count(c => c == '\n'));
    }

    [Fact]
    public async Task SetBranchAsync_WritesInPlaceAndRestoreReturnsPreviousContent()
    {
        var directory = Directory.CreateTempSubdirectory("barkcloud-compose");
        try
        {
            var composePath = Path.Combine(directory.FullName, "docker-compose.yml");
            var backupPath = Path.Combine(directory.FullName, "maintenance");
            await File.WriteAllTextAsync(composePath, Compose);

            await using var openedBeforeWrite = new FileStream(composePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var service = CreateService(composePath, backupPath);

            var previous = await service.SetBranchAsync("cloud-web", "nightly");

            previous.Should().Be(Compose);
            (await File.ReadAllTextAsync(composePath)).Should().Contain("barkcloud-web-nightly:latest");
            Directory.GetFiles(backupPath).Should().ContainSingle();

            await service.RestoreAsync(previous);
            (await File.ReadAllTextAsync(composePath)).Should().Be(Compose);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("docker.barkfluff.com/barkcloud-users-dev:latest", "dev")]
    [InlineData("docker.barkfluff.com/barkcloud-users-nightly:1.0.3", "nightly")]
    [InlineData("docker.barkfluff.com/barkcloud-users:latest", "master")]
    [InlineData("datalust/seq:latest", null)]
    public void BranchFromImage_DetectsChannel(string image, string? expected)
        => ComposeImageService.BranchFromImage(image).Should().Be(expected);

    private static ComposeImageService CreateService(string composePath, string backupPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Docker:ComposeFile"] = composePath,
                ["Docker:ComposeBackupDirectory"] = backupPath,
            })
            .Build();

        return new ComposeImageService(configuration, NullLogger<ComposeImageService>.Instance);
    }
}
