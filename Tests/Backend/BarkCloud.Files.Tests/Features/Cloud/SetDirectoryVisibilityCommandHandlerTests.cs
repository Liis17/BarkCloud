using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SetDirectoryVisibility;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.SetDirectoryVisibility;

public sealed class SetDirectoryVisibilityCommandHandlerTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task Handle_RecomputesDescendantsAndReturnsProperties()
    {
        using var database = new SqliteFilesContext();
        var parent = Directory("parent");
        var child = Directory("child", parent.Id);
        var grandchild = Directory("grandchild", child.Id);
        database.Context.CloudDirectories.AddRange(parent, child, grandchild);
        await database.Context.SaveChangesAsync();
        var handler = Handler(database.Context);

        var response = await handler.Handle(new SetDirectoryVisibilityCommand
        {
            DirectoryId = parent.Id,
            Inherit = false,
            HideFromGallery = true,
            HideFromSearch = true
        }, default);

        parent.InheritVisibility.Should().BeFalse();
        parent.EffectiveHideFromGallery.Should().BeTrue();
        child.EffectiveHideFromGallery.Should().BeTrue();
        grandchild.EffectiveHideFromSearch.Should().BeTrue();
        response.FileCount.Should().Be(0);
        response.FolderCount.Should().Be(2);
        response.HideFromGallery.Should().BeTrue();
        response.EffectiveHideFromSearch.Should().BeTrue();
        response.HasParent.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_SavesOwnSettingsWhenEffectiveValueDoesNotChange()
    {
        using var database = new SqliteFilesContext();
        var parent = Directory("parent", hideFromGallery: true, effectiveHideFromGallery: true);
        var child = Directory("child", parent.Id, hideFromGallery: true, effectiveHideFromGallery: true);
        database.Context.CloudDirectories.AddRange(parent, child);
        await database.Context.SaveChangesAsync();
        var handler = Handler(database.Context);

        await handler.Handle(new SetDirectoryVisibilityCommand
        {
            DirectoryId = child.Id,
            Inherit = false,
            HideFromGallery = true
        }, default);

        await using (var read = database.CreateAdditionalContext())
        {
            var saved = await read.CloudDirectories.SingleAsync(x => x.Id == child.Id);
            saved.InheritVisibility.Should().BeFalse();
            saved.HideFromGallery.Should().BeTrue();
            saved.EffectiveHideFromGallery.Should().BeTrue();
        }

        await handler.Handle(new SetDirectoryVisibilityCommand
        {
            DirectoryId = parent.Id,
            HideFromGallery = false
        }, default);

        child.EffectiveHideFromGallery.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_TopLevelInheritIsDerivedFromOwnVisibilityFlags()
    {
        using var database = new SqliteFilesContext();
        var directory = Directory("root");
        database.Context.CloudDirectories.Add(directory);
        await database.Context.SaveChangesAsync();
        var handler = Handler(database.Context);

        await handler.Handle(new SetDirectoryVisibilityCommand
        {
            DirectoryId = directory.Id,
            Inherit = true,
            HideFromSearch = true
        }, default);
        directory.InheritVisibility.Should().BeFalse();
        directory.EffectiveHideFromSearch.Should().BeTrue();

        await handler.Handle(new SetDirectoryVisibilityCommand { DirectoryId = directory.Id }, default);
        directory.InheritVisibility.Should().BeTrue();
        directory.EffectiveHideFromSearch.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MissingOrForeignDirectoryThrowsWithoutCommit()
    {
        using var database = new SqliteFilesContext();
        var foreign = Directory("foreign", ownerId: 999);
        database.Context.CloudDirectories.Add(foreign);
        await database.Context.SaveChangesAsync();
        var handler = Handler(database.Context);

        Func<Task> missing = () => handler.Handle(new SetDirectoryVisibilityCommand { DirectoryId = Guid.NewGuid() }, default);
        Func<Task> forbidden = () => handler.Handle(new SetDirectoryVisibilityCommand { DirectoryId = foreign.Id }, default);
        await missing.Should().ThrowAsync<DirectoryNotFoundException>();
        await forbidden.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    private static SetDirectoryVisibilityCommandHandler Handler(FilesContext context) =>
        new(new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId));

    private static CloudDirectory Directory(
        string name,
        Guid? parentId = null,
        long ownerId = OwnerId,
        bool inherit = true,
        bool hideFromGallery = false,
        bool hideFromSearch = false,
        bool effectiveHideFromGallery = false,
        bool effectiveHideFromSearch = false) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, ParentId = parentId, Name = name,
        InheritVisibility = inherit,
        HideFromGallery = hideFromGallery,
        HideFromSearch = hideFromSearch,
        EffectiveHideFromGallery = effectiveHideFromGallery,
        EffectiveHideFromSearch = effectiveHideFromSearch,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
}
