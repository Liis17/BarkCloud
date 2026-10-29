using BarkCloud.Transcoder;

namespace BarkCloud.Transcoder.Tests;

public sealed class FfmpegCommandTests
{
    [Theory]
    [InlineData(360, "36")]
    [InlineData(720, "34")]
    [InlineData(1080, "32")]
    [InlineData(1440, "30")]
    [InlineData(2160, "28")]
    public void BuildArgumentsUsesHeightSpecificCrf(int height, string crf)
    {
        var arguments = FfmpegCommand.BuildArguments("source", "output.webm", height, 4);

        Assert.Contains("libsvtav1", arguments);
        Assert.Contains("yuv420p10le", arguments);
        Assert.Contains("libopus", arguments);
        Assert.Equal(crf, arguments[Array.IndexOf(arguments.ToArray(), "-crf") + 1]);
        Assert.Equal("4", arguments[Array.IndexOf(arguments.ToArray(), "-threads") + 1]);
        Assert.Contains("lp=2", arguments);
        Assert.Contains("scale=w='if(gte(iw,ih),-2," + height + ")':h='if(gte(iw,ih)," + height + ",-2)':flags=lanczos", arguments);
    }

    [Fact]
    public void BuildArgumentsRejectsUnsupportedHeight()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegCommand.BuildArguments("in", "out", 480, 2));
    }
}
