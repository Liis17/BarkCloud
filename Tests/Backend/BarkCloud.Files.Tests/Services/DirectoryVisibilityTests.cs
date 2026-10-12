using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.CreateDirectory;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Services;

public sealed class DirectoryVisibilityTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task RecomputeSubtreeAsync_InheritsAndHonorsOverride()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root", hideFromGallery: true, hideFromSearch: true);
        var child = Directory("child", parentId: root.Id);
        var open = Directory("open", parentId: child.Id, inherit: false);
        var leaf = Directory("leaf", parentId: open.Id);
        database.Context.CloudDirectories.AddRange(root, child, open, leaf);
        await database.Context.SaveChangesAsync();

        await DirectoryVisibility.RecomputeSubtreeAsync(new CloudHierarchyStorage(database.Context), OwnerId, root);

        root.EffectiveHideFromGallery.Should().BeTrue();
        root.EffectiveHideFromSearch.Should().BeTrue();
        child.EffectiveHideFromGallery.Should().BeTrue();
        child.EffectiveHideFromSearch.Should().BeTrue();
        open.EffectiveHideFromGallery.Should().BeFalse();
        open.EffectiveHideFromSearch.Should().BeFalse();
        leaf.EffectiveHideFromGallery.Should().BeFalse();
        leaf.EffectiveHideFromSearch.Should().BeFalse();
    }

    [Fact]
    public async Task RecomputeSubtreeAsync_PersistsOwnOverrideWhenEffectiveValueIsUnchanged()
    {
        using var database = new SqliteFilesContext();
        var parent = Directory("parent", hideFromGallery: true);
        var child = Directory("child", parentId: parent.Id);
        database.Context.CloudDirectories.AddRange(parent, child);
        await database.Context.SaveChangesAsync();
        var storage = new CloudHierarchyStorage(database.Context);

        await DirectoryVisibility.RecomputeSubtreeAsync(storage, OwnerId, parent);
        child.InheritVisibility = false;
        child.HideFromGallery = true;
        await DirectoryVisibility.RecomputeSubtreeAsync(storage, OwnerId, child);
        await storage.SaveChangesAsync();

        await using (var read = database.CreateAdditionalContext())
        {
            var saved = await read.CloudDirectories.FindAsync(child.Id);
            saved!.InheritVisibility.Should().BeFalse();
            saved.HideFromGallery.Should().BeTrue();
            saved.EffectiveHideFromGallery.Should().BeTrue();
        }

        parent.HideFromGallery = false;
        await DirectoryVisibility.RecomputeSubtreeAsync(storage, OwnerId, parent);

        child.EffectiveHideFromGallery.Should().BeTrue();
        child.InheritVisibility.Should().BeFalse();
    }

    [Fact]
    public async Task MoveDirectory_RecomputesSubtreeWhenMovingIntoAndOutOfHiddenParent()
    {
        using var database = new SqliteFilesContext();
        var hidden = Directory("hidden", hideFromGallery: true, hideFromSearch: true);
        var open = Directory("open");
        var moved = Directory("moved");
        var child = Directory("child", parentId: moved.Id);
        database.Context.CloudDirectories.AddRange(hidden, open, moved, child);
        await database.Context.SaveChangesAsync();
        var hierarchy = new CloudHierarchyStorage(database.Context);
        await DirectoryVisibility.RecomputeSubtreeAsync(hierarchy, OwnerId, hidden);
        await hierarchy.SaveChangesAsync();
        var handler = new MoveDirectoryCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId),
            NullLogger<MoveDirectoryCommandHandler>.Instance);

        await handler.Handle(new MoveDirectoryCommand { DirectoryId = moved.Id, NewParentId = hidden.Id }, default);
        moved.EffectiveHideFromGallery.Should().BeTrue();
        child.EffectiveHideFromSearch.Should().BeTrue();

        await handler.Handle(new MoveDirectoryCommand { DirectoryId = moved.Id, NewParentId = open.Id }, default);
        moved.EffectiveHideFromGallery.Should().BeFalse();
        moved.EffectiveHideFromSearch.Should().BeFalse();
        child.EffectiveHideFromGallery.Should().BeFalse();
        child.EffectiveHideFromSearch.Should().BeFalse();
    }

    [Fact]
    public async Task CreateDirectory_CopiesEffectiveVisibilityFromParent()
    {
        using var database = new SqliteFilesContext();
        var parent = Directory("hidden", hideFromGallery: true, hideFromSearch: true);
        database.Context.CloudDirectories.Add(parent);
        await database.Context.SaveChangesAsync();
        var hierarchy = new CloudHierarchyStorage(database.Context);
        await DirectoryVisibility.RecomputeSubtreeAsync(hierarchy, OwnerId, parent);
        await hierarchy.SaveChangesAsync();
        var handler = new CreateDirectoryCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId),
            NullLogger<CreateDirectoryCommandHandler>.Instance);

        var created = await handler.Handle(new CreateDirectoryCommand { ParentId = parent.Id, Name = "child" }, default);

        var child = await database.Context.CloudDirectories.FindAsync(Guid.Parse(created.Id));
        child!.InheritVisibility.Should().BeTrue();
        child.EffectiveHideFromGallery.Should().BeTrue();
        child.EffectiveHideFromSearch.Should().BeTrue();
    }

    [Fact]
    public async Task RecomputeSubtreeAsync_CycleAndSelfParentThrowWithoutChangingFlags()
    {
        foreach (var selfParent in new[] { false, true })
        {
            using var database = new SqliteFilesContext();
            var root = Directory("root", effectiveHideFromGallery: true);
            var child = Directory("child", parentId: root.Id, effectiveHideFromSearch: true);
            root.ParentId = selfParent ? root.Id : child.Id;
            database.Context.CloudDirectories.AddRange(root, child);
            await database.Context.SaveChangesAsync();

            var act = () => DirectoryVisibility.RecomputeSubtreeAsync(
                new CloudHierarchyStorage(database.Context), OwnerId, root);

            await act.Should().ThrowAsync<CircularMoveException>();
            root.EffectiveHideFromGallery.Should().BeTrue();
            root.EffectiveHideFromSearch.Should().BeFalse();
            child.EffectiveHideFromGallery.Should().BeFalse();
            child.EffectiveHideFromSearch.Should().BeTrue();
        }
    }

    [Fact]
    public async Task RecomputeSubtreeAsync_MissingParentThrows()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("orphan", parentId: Guid.NewGuid());
        database.Context.CloudDirectories.Add(root);
        await database.Context.SaveChangesAsync();

        var act = () => DirectoryVisibility.RecomputeSubtreeAsync(
            new CloudHierarchyStorage(database.Context), OwnerId, root);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    private static CloudDirectory Directory(
        string name,
        Guid? parentId = null,
        bool inherit = true,
        bool hideFromGallery = false,
        bool hideFromSearch = false,
        bool effectiveHideFromGallery = false,
        bool effectiveHideFromSearch = false) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = OwnerId,
        ParentId = parentId,
        Name = name,
        InheritVisibility = inherit,
        HideFromGallery = hideFromGallery,
        HideFromSearch = hideFromSearch,
        EffectiveHideFromGallery = effectiveHideFromGallery,
        EffectiveHideFromSearch = effectiveHideFromSearch,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
