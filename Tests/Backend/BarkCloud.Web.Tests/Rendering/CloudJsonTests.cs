using BarkCloud.Proto.Files;
using BarkCloud.Web.Rendering;

namespace BarkCloud.Web.Tests.Rendering;

public class CloudJsonTests
{
    private static UploadFileInfo SampleFile() => new()
    {
        Id = "f1",
        FileName = "photo.jpg",
        FileSize = 2048,
        MediaKind = MediaKind.Photo,
        ImageWidth = 1920,
        ImageHeight = 1080
    };

    [Fact]
    public void Media_BuildsCardWithFileMetadata()
    {
        var card = (Dictionary<string, object?>)CloudJson.Media(SampleFile());

        card["id"].Should().Be("f1");
        card["name"].Should().Be("photo.jpg");
        card["ext"].Should().Be("JPG");
        card["kind"].Should().Be("photo");
        card["iconKind"].Should().Be("img");
        card["size"].Should().Be(2048L);
        card["sizeLabel"].Should().Be("2 КБ");
        card["width"].Should().Be(1920);
        card["height"].Should().Be(1080);
    }

    [Fact]
    public void Media_PreviewsOnlyWithUrl_OrderedByTargetWidth()
    {
        var file = SampleFile();
        file.Previews.Add(new FilePreviewInfo { TargetWidth = 1024, ActualWidth = 1000, PreviewUrl = "u1024" });
        file.Previews.Add(new FilePreviewInfo { TargetWidth = 128, ActualWidth = 120, PreviewUrl = "u128" });
        file.Previews.Add(new FilePreviewInfo { TargetWidth = 512, PreviewUrl = "" }); // без URL — отфильтровывается

        var card = (Dictionary<string, object?>)CloudJson.Media(file);
        var previews = ((System.Collections.IEnumerable)card["previews"]!).Cast<object>().ToArray();

        previews.Should().HaveCount(2);
        previews[0].GetType().GetProperty("target")!.GetValue(previews[0]).Should().Be(128);
        previews[1].GetType().GetProperty("target")!.GetValue(previews[1]).Should().Be(1024);
        previews[0].GetType().GetProperty("w")!.GetValue(previews[0]).Should().Be(120);
    }

    [Fact]
    public void MediaItem_AddsEntryFields()
    {
        var item = new UserImageItem
        {
            File = SampleFile(),
            EntriesCount = 2,
            EntryNames = { "a.jpg", "b.jpg" },
            EntryIds = { "e1", "e2" }
        };

        var card = (Dictionary<string, object?>)CloudJson.MediaItem(item);

        card["entriesCount"].Should().Be(2);
        ((string[])card["entryNames"]!).Should().Equal("a.jpg", "b.jpg");
        ((string[])card["entryIds"]!).Should().Equal("e1", "e2");
        card["name"].Should().Be("photo.jpg");
    }

    [Theory]
    [InlineData(MediaKind.Photo, "photo")]
    [InlineData(MediaKind.Video, "video")]
    [InlineData(MediaKind.Document, "document")]
    [InlineData(MediaKind.Audio, "audio")]
    [InlineData(MediaKind.Other, "other")]
    public void Media_MapsMediaKindName(MediaKind kind, string expected)
    {
        var file = SampleFile();
        file.MediaKind = kind;
        var card = (Dictionary<string, object?>)CloudJson.Media(file);
        card["kind"].Should().Be(expected);
    }
}
