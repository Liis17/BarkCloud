using BarkCloud.Proto.Files;
using BarkCloud.Web.Endpoints;

namespace BarkCloud.Web.Tests.Endpoints;

public class CloudApiEndpointsTests
{
    [Theory]
    [InlineData("media")]
    [InlineData("photo")]
    [InlineData("video")]
    public void BuildSearchFilesRequest_GalleryKindsExcludeHiddenFolders(string kind)
    {
        var request = CloudApiEndpoints.BuildSearchFilesRequest("bird", kind, null, null, null);

        request.ExcludeHiddenFromGallery.Should().BeTrue();
    }

    [Fact]
    public void BuildSearchFilesRequest_WithoutKindDoesNotExcludeHiddenFolders()
    {
        var request = CloudApiEndpoints.BuildSearchFilesRequest("bird", null, null, null, null);

        request.ExcludeHiddenFromGallery.Should().BeFalse();
        request.KindFilter.Should().BeEmpty();
    }
}
