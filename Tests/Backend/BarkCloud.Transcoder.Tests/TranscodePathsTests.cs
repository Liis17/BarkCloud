using BarkCloud.Transcoder;

namespace BarkCloud.Transcoder.Tests;

public sealed class TranscodePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly TranscodePaths _paths;

    public TranscodePathsTests()
    {
        _paths = new TranscodePaths(_root);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("folder/../../outside")]
    public void ResolveInputRejectsParentSegments(string path)
    {
        Assert.Throws<ArgumentException>(() => _paths.ResolveInput(path));
    }

    [Fact]
    public void ResolveInputRejectsRootedPath()
    {
        Assert.Throws<ArgumentException>(() => _paths.ResolveInput(Path.GetFullPath(Path.Combine(_root, "video"))));
    }

    [Fact]
    public void ResolveOutputCreatesDirectoryUnderRoot()
    {
        var output = _paths.ResolveOutput("video/720p.webm");

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "video", "720p.webm")), output);
        Assert.True(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Fact]
    public void ResolveInputAcceptsExistingFileUnderRoot()
    {
        var input = Path.Combine(_root, "source");
        File.WriteAllText(input, "source");

        Assert.Equal(input, _paths.ResolveInput("source"));
    }

    [Fact]
    public void ResolveOutputRejectsSymlinkOutsideRoot()
    {
        Directory.CreateDirectory(_outside);
        var link = Path.Combine(_root, "outside");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => _paths.ResolveOutput("outside/variant.webm"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_outside))
        {
            Directory.Delete(_outside, recursive: true);
        }
    }
}
