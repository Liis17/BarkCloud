using BarkCloud.Web.Rendering;

namespace BarkCloud.Web.Tests.Rendering;

public class FileKindTests
{
    [Theory]
    [InlineData("photo.jpg", "img", "JPG")]
    [InlineData("Photo.JPEG", "img", "JPEG")]
    [InlineData("clip.mp4", "vid", "MP4")]
    [InlineData("movie.MOV", "vid", "MOV")]
    [InlineData("doc.pdf", "pdf", "PDF")]
    [InlineData("notes.txt", "doc", "TXT")]
    [InlineData("sheet.xlsx", "doc", "XLSX")]
    [InlineData("archive.zip", "zip", "ZIP")]
    [InlineData("script.ts", "code", "TS")]
    [InlineData("song.mp3", "audio", "MP3")]
    [InlineData("unknown.bin", "doc", "BIN")]
    public void Classify_MapsExtensionToKindAndUppercaseExt(string fileName, string kind, string ext)
    {
        var result = FileKind.Classify(fileName);
        result.Kind.Should().Be(kind);
        result.Ext.Should().Be(ext);
    }

    [Theory]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    public void Classify_NoExtension_FallsBackToFile(string fileName)
        => FileKind.Classify(fileName).Ext.Should().Be("FILE");

    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("clip.webm", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("doc.pdf", false)]
    public void IsVideo_TrueOnlyForVideoKind(string fileName, bool expected)
        => FileKind.IsVideo(fileName).Should().Be(expected);
}
