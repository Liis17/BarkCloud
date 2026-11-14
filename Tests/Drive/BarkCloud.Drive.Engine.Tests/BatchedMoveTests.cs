using BarkCloud.Drive.Engine.Tests.Fakes;

using DokanNet;

using FluentAssertions;

using Microsoft.Extensions.Time.Testing;

using Xunit.Abstractions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class BatchedMoveTests(ITestOutputHelper output)
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
    public void Unreachable_server_gives_up_after_retries_and_shows_the_real_server_state()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo());
        for (var i = 0; i < 3; i++)
            cloud.Invoker.FailNext(FakeCloudMethods.MoveFileEntries,
                new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "offline")));

        gateway.FlushPendingMoves();

        CountCalls(cloud, FakeCloudMethods.MoveFileEntries).Should().Be(3);
        gateway.PendingMoveCount.Should().Be(0);
        gateway.ListDirectory(source).Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
        gateway.ListDirectory(destination).Files.Should().BeEmpty();
        fs.LastSyncError.Should().Contain("item.jpg").And.NotContain("возвращён");
    }

    [Fact]
    public async Task Background_refresh_started_before_an_own_patch_is_caught_up_instead_of_discarded()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, _) = AddFolders(cloud);
        var kept = cloud.AddFile(source, "kept.jpg", [1]);
        var deleted = cloud.AddFile(source, "deleted.jpg", [1]);
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        gateway.ListDirectory(source);
        time.Advance(TimeSpan.FromSeconds(6));

        var refreshRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new ManualResetEventSlim();
        cloud.Invoker.AfterResponse = (method, request) =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed
                || ((BarkCloud.Proto.Files.ListDirectoryRequest)request).DirectoryId != source)
                return;
            refreshRead.TrySetResult();
            releaseRefresh.Wait(TimeSpan.FromSeconds(5));
        };
        var refreshFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ListingLoadFinishedForTesting = directoryId =>
        {
            if (directoryId == source)
                refreshFinished.TrySetResult();
        };
        var staleAt = gateway.ListingCachedAtForTesting(source);
        gateway.ListDirectory(source); // устаревший снимок → фоновое обновление
        await refreshRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gateway.QueueDeleteFileEntry(deleted.EntryId, source); // правка снимка во время загрузки
        gateway.FlushPending(throwOnFailure: true);
        cloud.Invoker.AfterResponse = null;
        releaseRefresh.Set();
        await refreshFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Результат загрузки догнан правкой и закеширован: снимок свежий, удалённого файла нет.
        gateway.ListingCachedAtForTesting(source).Should().Be(time.GetUtcNow().UtcDateTime).And.NotBe(staleAt);
        gateway.ListDirectory(source).Files.Select(file => file.Entry.Id).Should().Equal(kept.EntryId);
        CountCalls(cloud, FakeCloudMethods.ListDirectoryDetailed).Should().Be(2);
    }

    [Fact]
    public async Task Background_refresh_overtaken_by_an_invalidation_is_discarded()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, _) = AddFolders(cloud);
        cloud.AddFile(source, "a.jpg", [1]);
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        gateway.ListDirectory(source);
        time.Advance(TimeSpan.FromSeconds(6));
        var refreshRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new ManualResetEventSlim();
        var refreshFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.AfterResponse = (method, request) =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            refreshRead.TrySetResult();
            releaseRefresh.Wait(TimeSpan.FromSeconds(5));
        };
        gateway.ListingLoadFinishedForTesting = _ => refreshFinished.TrySetResult();
        gateway.ListDirectory(source);
        await refreshRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gateway.InvalidateListing(source);
        cloud.Invoker.AfterResponse = null;
        releaseRefresh.Set();
        await refreshFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gateway.ListingCachedAtForTesting(source).Should().BeNull("ответ мог устареть после инвалидации");
    }

    [Fact]
    public void Deleting_a_folder_after_its_file_move_was_rejected_keeps_the_folder_and_the_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        var file = cloud.AddFile(source, "item.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        fs.MoveFile("\\Source\\item.jpg", "\\Destination\\item.jpg", false, new FakeDokanFileInfo());
        cloud.AddFile(destination, "item.jpg", [2]); // перенос будет отклонён конфликтом имени

        fs.DeleteDirectory("\\Source", new FakeDokanFileInfo { IsDirectory = true })
            .Should().Be(DokanResult.DirectoryNotEmpty);

        CountCalls(cloud, FakeCloudMethods.DeleteDirectory).Should().Be(0);
        gateway.ListDirectory(source).Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
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

    [Fact]
    public void Moving_5000_files_out_of_a_5000_file_folder_stays_fast_and_never_rereads_listings()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var (source, destination) = AddFolders(cloud);
        for (var i = 0; i < 5000; i++)
            cloud.AddFile(source, $"img-{i:D4}.jpg", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fs = new BarkCloudFileSystem(gateway, temp.Path("write"));
        Warm(gateway, source, destination);
        var listCalls = CountCalls(cloud, FakeCloudMethods.ListDirectoryDetailed);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
            fs.MoveFile($"\\Source\\img-{i:D4}.jpg", $"\\Destination\\img-{i:D4}.jpg", false, new FakeDokanFileInfo())
                .Should().Be(DokanResult.Success);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gateway.FlushPendingMoves();

        output.WriteLine($"moves=5000; elapsed={stopwatch.Elapsed.TotalMilliseconds:F0} ms " +
                         $"({stopwatch.Elapsed.TotalMilliseconds / 5000:F3} ms/move); allocated={allocated / 5000:N0} bytes/move; " +
                         $"MoveFileEntries={CountCalls(cloud, FakeCloudMethods.MoveFileEntries)}");
        CountCalls(cloud, FakeCloudMethods.ListDirectoryDetailed).Should().Be(listCalls);
        CountCalls(cloud, FakeCloudMethods.MoveFileEntries).Should().Be(50);
        gateway.ListDirectory(source).Files.Should().BeEmpty();
        gateway.ListDirectory(destination).Files.Should().HaveCount(5000);
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
