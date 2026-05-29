using BarkCloud.Files.Features.Cloud.ResolveShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainShareLink = BarkCloud.Files.Domain.ShareLink;
using TempFileEntity = BarkCloud.Files.Domain.TempFile;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ResolveShare;

public class ResolveShareCommandHandlerTests
{
    private readonly Mock<IShareStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ITempFilesStorage> _temp = new();

    private ResolveShareCommandHandler CreateSut() => new(
        _storage.Object, _files.Object, _temp.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ResolveShareCommandHandler>.Instance);

    [Fact]
    public async Task Handle_TokenNotFound_ReturnsNotFound()
    {
        _storage.Setup(s => s.GetByToken(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainShareLink?)null);

        var response = await CreateSut().Handle(new ResolveShareCommand { Token = "missing" }, default);

        response.Found.Should().BeFalse();
        _storage.Verify(s => s.IncrementClicks(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FileDeleted_ReturnsNotFound()
    {
        var share = new DomainShareLink { Id = Guid.NewGuid(), FileId = Guid.NewGuid(), Token = "t" };
        _storage.Setup(s => s.GetByToken("t", It.IsAny<CancellationToken>())).ReturnsAsync(share);
        _files.Setup(s => s.GetFile(share.FileId)).ReturnsAsync((UploadFileEntity?)null);

        var response = await CreateSut().Handle(new ResolveShareCommand { Token = "t" }, default);

        response.Found.Should().BeFalse();
        _storage.Verify(s => s.IncrementClicks(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_IncrementsClicksAndReturnsUrl()
    {
        var fileId = Guid.NewGuid();
        var tempId = Guid.NewGuid();
        var share = new DomainShareLink { Id = Guid.NewGuid(), FileId = fileId, Token = "t", Name = "Pic" };
        _storage.Setup(s => s.GetByToken("t", It.IsAny<CancellationToken>())).ReturnsAsync(share);
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId });
        _temp.Setup(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFileEntity> { new() { Id = tempId, OriginalFileId = fileId } });

        var response = await CreateSut().Handle(new ResolveShareCommand { Token = "t" }, default);

        response.Found.Should().BeTrue();
        response.FileId.Should().Be(fileId.ToString());
        response.Name.Should().Be("Pic");
        response.DownloadUrl.Should().Contain(tempId.ToString());
        _storage.Verify(s => s.IncrementClicks(share.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
