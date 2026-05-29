using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.CreateDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.CreateDirectory;

public class CreateDirectoryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private CreateDirectoryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<CreateDirectoryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new CreateDirectoryCommand { Name = "   " }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_ParentNotFound_Throws()
    {
        var parentId = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(parentId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new CreateDirectoryCommand { Name = "New", ParentId = parentId }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_ParentNotOwned_ThrowsAccessDenied()
    {
        var parentId = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = parentId, OwnerId = 999 });

        var act = () => CreateSut().Handle(new CreateDirectoryCommand { Name = "New", ParentId = parentId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_NameExists_Throws()
    {
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, null, "New", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new CreateDirectoryCommand { Name = "New" }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
    }

    [Fact]
    public async Task Handle_HappyPath_AddsAndReturnsInfo()
    {
        _storage.Setup(s => s.DirectoryNameExists(OwnerId, null, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var response = await CreateSut().Handle(new CreateDirectoryCommand { Name = "  Photos  " }, default);

        response.Name.Should().Be("Photos");
        response.ParentId.Should().BeEmpty();
        _storage.Verify(s => s.AddDirectory(
            It.Is<CloudDirectory>(d => d.OwnerId == OwnerId && d.Name == "Photos" && d.ParentId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
