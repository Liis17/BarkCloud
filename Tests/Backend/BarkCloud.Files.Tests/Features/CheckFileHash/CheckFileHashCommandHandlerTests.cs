using BarkCloud.Files.Features.CheckFileHash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.CheckFileHash;

public class CheckFileHashCommandHandlerTests
{
    private readonly Mock<IFileHashesStorage> _hashes = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private CheckFileHashCommandHandler CreateSut(long userId = 42) => new(
        _hashes.Object,
        _files.Object,
        UserContextFactory.Create(userId),
        NullLogger<CheckFileHashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyHash_ReturnsEmpty()
    {
        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = "" }, default);

        response.FileId.Should().BeEmpty();
        _hashes.Verify(s => s.GetFileIdByHash(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_InvalidHashFormat_ReturnsEmpty()
    {
        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = "not-a-hash" }, default);

        response.FileId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HashNotFound_ReturnsEmptyAndDoesNotTouchFiles()
    {
        var hash = new string('a', 64);
        _hashes.Setup(s => s.GetFileIdByHash(hash)).ReturnsAsync((Guid?)null);

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.FileId.Should().BeEmpty();
        _files.Verify(s => s.AddUploaderToFile(It.IsAny<Guid>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HashFound_ReturnsFileIdAndAddsUploader()
    {
        var fileId = Guid.NewGuid();
        var hash = new string('b', 64);
        _hashes.Setup(s => s.GetFileIdByHash(hash)).ReturnsAsync(fileId);

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.FileId.Should().Be(fileId.ToString());
        _files.Verify(s => s.AddUploaderToFile(fileId, 42), Times.Once);
    }

    [Fact]
    public async Task Handle_HashNormalizedToLowercase()
    {
        var fileId = Guid.NewGuid();
        var hash = new string('A', 64);
        _hashes.Setup(s => s.GetFileIdByHash(hash.ToLowerInvariant())).ReturnsAsync(fileId);

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.FileId.Should().Be(fileId.ToString());
    }
}
