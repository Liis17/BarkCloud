using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SetDirectoryAppearance;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.SetDirectoryAppearance;

public class SetDirectoryAppearanceCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();

    private SetDirectoryAppearanceCommandHandler CreateSut() => new(_storage.Object, UserContextFactory.Create(OwnerId));

    private CloudDirectory SetupDirectory(long ownerId = OwnerId, string? iconKey = null, string? iconColor = null)
    {
        var directory = new CloudDirectory { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "Музыка", IconKey = iconKey, IconColor = iconColor };
        _storage.Setup(s => s.GetDirectory(directory.Id, It.IsAny<CancellationToken>())).ReturnsAsync(directory);
        return directory;
    }

    [Theory]
    [InlineData("Music", "")]
    [InlineData("music_note", "")]
    [InlineData("-music", "")]
    [InlineData("music", "#12345")]
    [InlineData("music", "red")]
    [InlineData("music", "#GGGGGG")]
    public async Task Handle_InvalidKeyOrColor_ThrowsWithoutSaving(string iconKey, string iconColor)
    {
        var directory = SetupDirectory();

        var act = () => CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = directory.Id, IconKey = iconKey, IconColor = iconColor }, default);

        await act.Should().ThrowAsync<DirectoryAppearanceInvalidException>();
        _storage.Verify(s => s.UpdateDirectory(It.IsAny<CloudDirectory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_TooLongKey_Throws()
    {
        var directory = SetupDirectory();

        var act = () => CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = directory.Id, IconKey = new string('a', 65) }, default);

        await act.Should().ThrowAsync<DirectoryAppearanceInvalidException>();
    }

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectory(id, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = id, IconKey = "music" }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var directory = SetupDirectory(ownerId: 999);

        var act = () => CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = directory.Id, IconKey = "music" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        directory.IconKey.Should().BeNull();
    }

    [Fact]
    public async Task Handle_Valid_SavesNormalizedColorAndReturnsIt()
    {
        var directory = SetupDirectory();

        var result = await CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = directory.Id, IconKey = "music-note", IconColor = "#ff8a00" }, default);

        directory.IconKey.Should().Be("music-note");
        directory.IconColor.Should().Be("#FF8A00");
        result.IconKey.Should().Be("music-note");
        result.IconColor.Should().Be("#FF8A00");
        _storage.Verify(s => s.UpdateDirectory(directory, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyValues_ResetsToDefault()
    {
        var directory = SetupDirectory(iconKey: "music", iconColor: "#FF8A00");

        var result = await CreateSut().Handle(new SetDirectoryAppearanceCommand { DirectoryId = directory.Id }, default);

        directory.IconKey.Should().BeNull();
        directory.IconColor.Should().BeNull();
        result.IconKey.Should().BeEmpty();
        result.IconColor.Should().BeEmpty();
    }
}
