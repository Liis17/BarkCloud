using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListTrash;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListTrash;

public class ListTrashCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListTrashCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListTrashCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _storage.Setup(s => s.ListTrashedPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>());

        var response = await CreateSut().Handle(new ListTrashCommand { Limit = 50 }, default);

        response.Items.Should().BeEmpty();
        response.NextCursorEntryId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OrphanBlob_UsesPlaceholderFile()
    {
        var entryId = Guid.NewGuid();
        var orphanFile = Guid.NewGuid();
        _storage.Setup(s => s.ListTrashedPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainFileEntry>
            {
                new() { Id = entryId, OwnerId = OwnerId, FileId = orphanFile, Name = "gone.jpg", DeletedAt = DateTime.UtcNow, PurgeAt = DateTime.UtcNow.AddDays(14) }
            });
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListTrashCommand { Limit = 50 }, default);

        var item = response.Items.Should().ContainSingle().Subject;
        item.Entry.Id.Should().Be(entryId.ToString());
        item.File.Id.Should().Be(orphanFile.ToString());
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        var entries = Enumerable.Range(0, 3)
            .Select(i => new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Name = $"f{i}", DeletedAt = DateTime.UtcNow.AddMinutes(-i) })
            .ToList();
        _storage.Setup(s => s.ListTrashedPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());

        var response = await CreateSut().Handle(new ListTrashCommand { Limit = 2 }, default);

        response.Items.Should().HaveCount(2);
        response.NextCursorEntryId.Should().Be(entries[1].Id.ToString());
    }
}
