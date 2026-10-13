using BarkCloud.Files.Features.Cloud.SearchFiles;
using BarkCloud.Files.Host;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Tests.Host;

public sealed class CloudApiServiceTests
{
    [Fact]
    public async Task SearchFiles_PassesGalleryVisibilityFlagToCommand()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<SearchFilesCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchFilesResponse());
        var service = new CloudApiService(mediator.Object);

        await service.SearchFiles(new SearchFilesRequest
        {
            Query = "photo",
            ExcludeHiddenFromGallery = true
        }, null!);

        mediator.Invocations.Should().ContainSingle();
        mediator.Invocations[0].Arguments[0].Should().BeOfType<SearchFilesCommand>()
            .Which.ExcludeHiddenFromGallery.Should().BeTrue();
    }
}
