using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.CheckFileHash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.CheckFileHash;

public class CheckFileHashCommandHandlerTests
{
    private readonly Mock<IFileHashesStorage> _hashes = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    private CheckFileHashCommandHandler CreateSut(long userId = 42) => new(
        _hashes.Object,
        _hierarchy.Object,
        UserContextFactory.Create(userId),
        NullLogger<CheckFileHashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyHash_ReturnsEmpty()
    {
        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = "" }, default);

        response.FileId.Should().BeEmpty();
        response.Exists.Should().BeFalse();
        _hashes.Verify(s => s.GetFileIdsByHash(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_InvalidHashFormat_ReturnsEmpty()
    {
        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = "not-a-hash" }, default);

        response.FileId.Should().BeEmpty();
        response.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_HashNotFound_ReturnsNotExists()
    {
        var hash = new string('a', 64);
        _hashes.Setup(s => s.GetFileIdsByHash(hash, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid>());

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.FileId.Should().BeEmpty();
        response.Exists.Should().BeFalse();
        response.ExistingLocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HashFound_ReturnsExistsWithUserLocationAndNoSideEffect()
    {
        var fileId = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var hash = new string('b', 64);
        _hashes.Setup(s => s.GetFileIdsByHash(hash, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { fileId });
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(42, It.Is<IReadOnlyCollection<Guid>>(c => c.Contains(fileId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = entryId, OwnerId = 42, FileId = fileId, DirectoryId = dirId, Name = "photo.jpg" }
            });
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = 42, Name = "Фото" });

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.Exists.Should().BeTrue();
        response.FileId.Should().Be(fileId.ToString());
        response.ExistingLocations.Should().ContainSingle();
        var loc = response.ExistingLocations[0];
        loc.EntryId.Should().Be(entryId.ToString());
        loc.Name.Should().Be("photo.jpg");
        loc.DirectoryId.Should().Be(dirId.ToString());
        loc.DirectoryName.Should().Be("Фото");
    }

    [Fact]
    public async Task Handle_HashFoundButNoUserEntries_ReturnsNotExists()
    {
        // Контент есть в системе (например, чужой блоб с тем же хешем), но у пользователя записей нет.
        // Приватность: наличие чужого файла не раскрываем — отвечаем «не существует».
        var fileId = Guid.NewGuid();
        var hash = new string('c', 64);
        _hashes.Setup(s => s.GetFileIdsByHash(hash, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { fileId });
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(42, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>());

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.Exists.Should().BeFalse();
        response.FileId.Should().BeEmpty();
        response.ExistingLocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RootEntry_ReturnsEmptyDirectoryName()
    {
        var fileId = Guid.NewGuid();
        var hash = new string('d', 64);
        _hashes.Setup(s => s.GetFileIdsByHash(hash, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { fileId });
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(42, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = 42, FileId = fileId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "doc.txt" }
            });

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        var loc = response.ExistingLocations.Should().ContainSingle().Subject;
        loc.DirectoryId.Should().BeEmpty();
        loc.DirectoryName.Should().BeEmpty();
        _hierarchy.Verify(s => s.GetDirectoryAsNoTracking(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HashNormalizedToLowercase()
    {
        var fileId = Guid.NewGuid();
        var hash = new string('A', 64);
        _hashes.Setup(s => s.GetFileIdsByHash(hash.ToLowerInvariant(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { fileId });
        _hierarchy.Setup(s => s.GetLiveEntriesForFiles(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = 42, FileId = fileId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "file.bin" }
            });

        var response = await CreateSut().Handle(new CheckFileHashCommand { FileHash = hash }, default);

        response.FileId.Should().Be(fileId.ToString());
        response.Exists.Should().BeTrue();
    }
}
