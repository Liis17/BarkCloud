using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListMyShares;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainShareLink = BarkCloud.Files.Domain.ShareLink;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListMyShares;

public class ListMySharesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IShareStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _uploadedFiles = new();

    private ListMySharesCommandHandler CreateSut()
    {
        _uploadedFiles.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<UploadFileEntity>());
        _uploadedFiles.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        return new ListMySharesCommandHandler(
            _storage.Object,
            _uploadedFiles.Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<ListMySharesCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _storage.Setup(s => s.ListPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainShareLink>());

        var response = await CreateSut().Handle(new ListMySharesCommand { Limit = 50 }, default);

        response.Shares.Should().BeEmpty();
        response.NextCursorShareId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var shares = Enumerable.Range(0, 3)
            .Select(i => new DomainShareLink { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Token = $"t{i}", CreatedAt = DateTime.UtcNow.AddMinutes(-i) })
            .ToList();
        _storage.Setup(s => s.ListPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(shares);

        var response = await CreateSut().Handle(new ListMySharesCommand { Limit = 2 }, default);

        response.Shares.Should().HaveCount(2);
        response.NextCursorShareId.Should().Be(shares[1].Id.ToString());
    }
}
