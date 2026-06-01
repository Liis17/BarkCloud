using BarkCloud.Files.Helpers;

namespace BarkCloud.Files.Tests.Helpers;

public class UniqueNameResolverTests
{
    [Fact]
    public async Task ResolveAsync_NameFree_ReturnsDesired()
    {
        var result = await UniqueNameResolver.ResolveAsync("photo.jpg", (_, _) => Task.FromResult(false));

        result.Should().Be("photo.jpg");
    }

    [Fact]
    public async Task ResolveAsync_NameTaken_AppendsSuffixBeforeExtension()
    {
        var taken = new HashSet<string> { "photo.jpg", "photo (1).jpg" };

        var result = await UniqueNameResolver.ResolveAsync("photo.jpg", (n, _) => Task.FromResult(taken.Contains(n)));

        result.Should().Be("photo (2).jpg");
    }

    [Fact]
    public async Task ResolveAsync_NoExtension_AppendsSuffix()
    {
        var taken = new HashSet<string> { "report" };

        var result = await UniqueNameResolver.ResolveAsync("report", (n, _) => Task.FromResult(taken.Contains(n)));

        result.Should().Be("report (1)");
    }
}
