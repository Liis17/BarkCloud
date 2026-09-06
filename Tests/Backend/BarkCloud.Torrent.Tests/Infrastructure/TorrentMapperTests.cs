using BarkCloud.Torrent.Infrastructure;

using ApiPriority = BarkCloud.Proto.Torrent.TorrentFilePriority;
using EnginePriority = MonoTorrent.Priority;

namespace BarkCloud.Torrent.Tests.Infrastructure;

public class TorrentMapperTests
{
    [Theory]
    [InlineData(EnginePriority.DoNotDownload, ApiPriority.Skip)]
    [InlineData(EnginePriority.Lowest, ApiPriority.Low)]
    [InlineData(EnginePriority.Low, ApiPriority.Low)]
    [InlineData(EnginePriority.Normal, ApiPriority.Normal)]
    [InlineData(EnginePriority.High, ApiPriority.High)]
    [InlineData(EnginePriority.Highest, ApiPriority.High)]
    [InlineData(EnginePriority.Immediate, ApiPriority.High)]
    public void ToProtoPriority_MapsMonoTorrentValuesToApiValues(EnginePriority source, ApiPriority expected)
        => TorrentMapper.ToProtoPriority(source).Should().Be(expected);

    [Theory]
    [InlineData(ApiPriority.Skip, EnginePriority.DoNotDownload)]
    [InlineData(ApiPriority.Low, EnginePriority.Low)]
    [InlineData(ApiPriority.Normal, EnginePriority.Normal)]
    [InlineData(ApiPriority.High, EnginePriority.High)]
    public void ToMonoTorrentPriority_MapsApiValuesToMonoTorrentValues(ApiPriority source, EnginePriority expected)
        => TorrentMapper.ToMonoTorrentPriority(source).Should().Be(expected);
}
