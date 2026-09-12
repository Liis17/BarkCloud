using System.Text;

using BarkCloud.Files.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class FileMetadataExtractorTests
{
    private readonly FileMetadataExtractor _sut = new(NullLogger<FileMetadataExtractor>.Instance);

    [Fact]
    public void ExtractFromPdf_WhenStrictAndContentIsInvalid_Throws()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a PDF"));

        var act = () => _sut.ExtractFromPdf(stream, rejectInvalid: true);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ExtractFromOffice_WhenStrictAndContentIsInvalid_Throws()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not an OpenXML package"));

        var act = () => _sut.ExtractFromOffice(
            stream,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            rejectInvalid: true);

        act.Should().Throw<InvalidDataException>();
    }
}
