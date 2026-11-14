using BarkCloud.Drive.Engine.Tests.Fakes;

using DokanNet;

using FluentAssertions;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class BatchedMoveTests
{
    [Fact]
    public void Bulk_move_answers_immediately_and_sends_batches_of_one_hundred()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        for (var i = 0; i < 250; i++)
            cloud.AddFile(source, $"img-{i:D3}.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        var listCalls = CountCalls(cloud, FakeCloudMethods.ListDirectoryDetailed);

        for (var i = 0; i < 250; i++)
            fs.MoveFile($"\\Source\\img-{i:D3}.jpg", $"\\Destination\\img-{i:D3}.jpg", false, new FakeDokanFileInfo())
                .Should().Be(DokanResult.Success);

        CountCalls(cloud, FakeCloudMethods.MoveFileEntry).Should().Be(0);
        CountCalls(cloud, FakeCloudMethods.MoveFileEntries).Should().Be(2, "по пачке на каждые 100 переносов");
        gateway.PendingMoveCount.Should().Be(50);
        gateway.ListDirectory(source).Files.Should().BeEmpty();
        gateway.ListDirectory(destination).Files.Should().HaveCount(250);
        CountCalls(cloud, FakeCloudMethods.ListDirectoryDetailed).Should().Be(listCalls);

        gateway.FlushPendingMoves();

        CountCalls(cloud, FakeCloudMethods.MoveFileEntries).Should().Be(3);
        gateway.PendingMoveCount.Should().Be(0);
        gateway.InvalidateListing(source);
        gateway.InvalidateListing(destination);
        gateway.ListDirectory(source).Files.Should().BeEmpty();
        gateway.ListDirectory(destination).Files.Should().HaveCount(250);
        fs.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void Rejected_move_returns_the_file_to_its_source_and_reports_the_error()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);

        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);
        gateway.ListDirectory(destination).Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
        var rival = cloud.AddFile(destination, "item.jpg", [2]); // другой клиент успел занять имя

        gateway.FlushPendingMoves();

        gateway.PendingMoveCount.Should().Be(0);
        gateway.ListDirectory(source).Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
        gateway.ListDirectory(destination).Files.Should().ContainSingle(entry => entry.Entry.Id == rival.EntryId);
        fs.LastSyncError.Should().Contain("item.jpg");
    }

    [Fact]
    public void Old_server_without_batch_move_falls_back_to_single_moves()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud { MoveFileEntriesUnsupported = true };
        var (source, destination) = AddFolders(cloud);
        cloud.AddFile(source, "a.jpg", [1]);
        cloud.AddFile(source, "b.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);

        fs.MoveFile("\\Source\\a.jpg", "\\Destination\\a.jpg", false, new FakeDokanFileInfo());
        fs.MoveFile("\\Source\\b.jpg", "\\Destination\\b.jpg", false, new FakeDokanFileInfo());
        gateway.FlushPendingMoves();

        CountCalls(cloud, FakeCloudMethods.MoveFileEntry).Should().Be(2);
        gateway.InvalidateListing(destination);
        gateway.ListDirectory(destination).Files.Should().HaveCount(2);
        fs.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void Listing_reloaded_before_the_batch_is_sent_still_shows_the_moved_file_in_its_destination()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo());

        gateway.InvalidateListing(source);
        gateway.InvalidateListing(destination);

        gateway.ListDirectory(source).Files.Should().BeEmpty();
        gateway.ListDirectory(destination).Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
        gateway.Resolve("\\Destination\\item.jpg")!.EntryId.Should().Be(file.EntryId);
        gateway.Resolve("\\Source\\item.jpg").Should().BeNull();
    }

    [Fact]
    public void Deleting_a_file_waiting_for_its_move_sends_the_move_first()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo());

        gateway.QueueDeleteFileEntry(file.EntryId, destination);
        gateway.FlushPending(throwOnFailure: true);

        var calls = cloud.Invoker.Calls.Select(call => call.MethodFullName)
            .Where(method => method is FakeCloudMethods.MoveFileEntries or FakeCloudMethods.DeleteFileEntries)
            .ToArray();
        calls.Should().Equal(FakeCloudMethods.MoveFileEntries, FakeCloudMethods.DeleteFileEntries);
        fs.LastSyncError.Should().BeNull();
        gateway.ListDirectory(destination).Files.Should().BeEmpty();
    }

    [Fact]
    public void Rename_of_a_file_waiting_for_its_move_happens_after_the_move()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo());

        fs.MoveFile("\\Destination\\item.jpg", "\\Destination\\renamed.jpg", false, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        gateway.PendingMoveCount.Should().Be(0);
        gateway.InvalidateListing(destination);
        gateway.ListDirectory(destination).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId && entry.Entry.Name == "renamed.jpg");
        fs.LastSyncError.Should().BeNull();
    }

    private static (string Source, string Destination) AddFolders(FakeCloud cloud)
        => (cloud.AddDirectory("Source"), cloud.AddDirectory("Destination"));

    private static void Warm(CloudGateway gateway, string source, string destination)
    {
        gateway.ListDirectory("");
        gateway.ListDirectory(source);
        gateway.ListDirectory(destination);
    }

    private static int CountCalls(FakeCloud cloud, string method)
        => cloud.Invoker.Calls.Count(call => call.MethodFullName == method);
}
