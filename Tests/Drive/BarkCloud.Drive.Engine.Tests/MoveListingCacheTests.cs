using System.Collections;
using System.Diagnostics;

using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using DokanNet;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class MoveListingCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void MoveFile_reuses_listing_cache_for_5000_cloud_moves()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        for (var i = 0; i < 100; i++)
        {
            cloud.AddFile(sourceDirectoryId, $"source-{i:D3}.bin");
            cloud.AddFile(destinationDirectoryId, $"destination-{i:D3}.bin");
        }
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        gateway.ListDirectory("");
        var originalSource = gateway.ListDirectory(sourceDirectoryId);
        var originalDestination = gateway.ListDirectory(destinationDirectoryId);
        var warmListCalls = CountListCalls(cloud);
        long copiedTicks = 0;
        long copiedBytes = 0;
        var copyCount = 0;
        DirectoryListingDetailed? firstDestinationSnapshot = null;
        gateway.ListingMoveSnapshotCopiedForTesting = (ticks, bytes) =>
        {
            copiedTicks += ticks;
            copiedBytes += bytes;
            copyCount++;
        };

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++)
        {
            var movingFromSource = (i & 1) == 0;
            var fromDirectoryId = movingFromSource ? sourceDirectoryId : destinationDirectoryId;
            var toDirectoryId = movingFromSource ? destinationDirectoryId : sourceDirectoryId;
            var fromDirectoryName = movingFromSource ? "Source" : "Destination";
            var toDirectoryName = movingFromSource ? "Destination" : "Source";

            fileSystem.MoveFile($"\\{fromDirectoryName}\\item.bin", $"\\{toDirectoryName}\\item.bin",
                replace: false, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

            var fromListing = gateway.ListDirectory(fromDirectoryId);
            var toListing = gateway.ListDirectory(toDirectoryId);
            fromListing.Files.Should().HaveCount(100);
            toListing.Files.Should().HaveCount(101);
            toListing.Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
            if (i == 0)
            {
                originalSource.Files.Should().HaveCount(101);
                firstDestinationSnapshot = toListing;
            }
            else if (i == 1)
                firstDestinationSnapshot!.Files.Should().HaveCount(101);
        }
        stopwatch.Stop();

        var finalListCalls = CountListCalls(cloud);
        finalListCalls.Should().Be(warmListCalls);
        copyCount.Should().Be(5_000);
        originalSource.Files.Should().HaveCount(101);
        originalSource.Files.Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);
        originalDestination.Files.Should().HaveCount(100);
        firstDestinationSnapshot!.Files.Should().HaveCount(101);

        output.WriteLine($"moves=5000; ListDirectoryDetailed RPC: {warmListCalls} initial, " +
                         $"{finalListCalls - warmListCalls} after warm-up; elapsed={stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
                         $"snapshot-copy={TimeSpan.FromSeconds(copiedTicks / (double)Stopwatch.Frequency).TotalMilliseconds:F1} ms total, " +
                         $"{copiedBytes:N0} bytes total ({copiedBytes / copyCount:N0} bytes/move)");
    }

    [Fact]
    public void MoveFile_rebinds_case_insensitive_name_index_when_moving_winner()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        cloud.AddFile(sourceDirectoryId, "A.txt", [1]);
        var remainingFile = cloud.AddFile(sourceDirectoryId, "a.txt", [2]);
        cloud.AddFile(destinationDirectoryId, "b.txt", [3]);
        cloud.AddFile(destinationDirectoryId, "z.txt", [4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);

        fileSystem.MoveFile("\\Source\\A.txt", "\\Destination\\A.txt", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

        var sourceListing = gateway.ListDirectory(sourceDirectoryId);
        gateway.FindFileByName(sourceDirectoryId, sourceListing, "a.txt")!
            .Entry.Id.Should().Be(remainingFile.EntryId);
        gateway.FindFileByName(sourceDirectoryId, sourceListing, "A.txt")!
            .Entry.Id.Should().Be(remainingFile.EntryId);
        var filesByName = CachedFileIndex(gateway, sourceDirectoryId, "FilesByName");
        filesByName["a.txt"].Entry.Id.Should().Be(remainingFile.EntryId);
        filesByName["A.txt"].Entry.Id.Should().Be(remainingFile.EntryId);
        var filesByExactName = CachedFileIndex(gateway, sourceDirectoryId, "FilesByExactName");
        filesByExactName.Should().NotContainKey("A.txt");
        filesByExactName["a.txt"].Entry.Id.Should().Be(remainingFile.EntryId);

        var destinationListing = gateway.ListDirectory(destinationDirectoryId);
        destinationListing.Files.Should().ContainSingle(file => file.Entry.Name == "A.txt");
        destinationListing.Files.Select(file => file.Entry.Name)
            .Should().Equal("A.txt", "b.txt", "z.txt");
    }

    [Fact]
    public void MoveFile_preserves_case_variant_name_indexes()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        cloud.AddFile(sourceDirectoryId, "A.txt", [1]);
        cloud.AddFile(sourceDirectoryId, "a.txt", [2]);
        cloud.AddFile(destinationDirectoryId, "b.txt", [3]);
        cloud.AddFile(destinationDirectoryId, "z.txt", [4]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        gateway.ListDirectory("");
        gateway.ListDirectory(sourceDirectoryId);
        gateway.ListDirectory(destinationDirectoryId);

        fileSystem.MoveFile("\\Source\\a.txt", "\\Destination\\a.txt", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

        var sourceListing = gateway.ListDirectory(sourceDirectoryId);
        gateway.FindFileByName(sourceDirectoryId, sourceListing, "a.txt")!
            .Entry.Name.Should().Be("A.txt");
        gateway.FindFileByName(sourceDirectoryId, sourceListing, "a.txt", exactFirst: true)!
            .Entry.Name.Should().Be("A.txt");
        gateway.FindFileByName(sourceDirectoryId, sourceListing, "A.txt", exactFirst: true)!
            .Entry.Name.Should().Be("A.txt");
        var filesByName = CachedFileIndex(gateway, sourceDirectoryId, "FilesByName");
        filesByName["a.txt"].Entry.Name.Should().Be("A.txt");
        var filesByExactName = CachedFileIndex(gateway, sourceDirectoryId, "FilesByExactName");
        filesByExactName.Should().NotContainKey("a.txt");
        filesByExactName["A.txt"].Entry.Name.Should().Be("A.txt");

        var destinationListing = gateway.ListDirectory(destinationDirectoryId);
        destinationListing.Files.Should().ContainSingle(file => file.Entry.Name == "a.txt");
        destinationListing.Files.Select(file => file.Entry.Name)
            .Should().Equal("a.txt", "b.txt", "z.txt");
    }

    [Fact]
    public void MoveFile_keeps_the_original_listing_ttl()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        gateway.ListDirectory("");
        gateway.ListDirectory(sourceDirectoryId);
        gateway.ListDirectory(destinationDirectoryId);
        time.Advance(TimeSpan.FromSeconds(4));

        fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should().ContainSingle();
        CountListCalls(cloud).Should().Be(3);

        time.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should().ContainSingle();
        CountListCalls(cloud).Should().Be(5);
    }

    [Fact]
    public async Task MoveFile_publishes_a_new_snapshot_without_overwriting_an_in_flight_listing()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        gateway.ListDirectory("");
        gateway.ListDirectory(sourceDirectoryId);
        gateway.ListDirectory(destinationDirectoryId);

        var staleResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DirectoryListingDetailed>? staleLoad = null;
        cloud.Invoker.AfterResponse += (method, request) =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed
                || ((ListDirectoryRequest)request).DirectoryId != sourceDirectoryId)
                return;
            staleResponseReady.TrySetResult();
            releaseStaleResponse.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.MoveFileEntry)
                return;
            time.Advance(TimeSpan.FromSeconds(6));
            staleLoad = Task.Factory.StartNew(() => gateway.ListDirectory(sourceDirectoryId),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            staleResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };

        try
        {
            gateway.MoveFileEntryAndUpdateListings(file.EntryId, sourceDirectoryId, destinationDirectoryId);
        }
        finally
        {
            releaseStaleResponse.TrySetResult();
        }

        (await staleLoad!.WaitAsync(TimeSpan.FromSeconds(10))).Files
            .Should().ContainSingle(entry => entry.Entry.Id == file.EntryId);

        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId);
        CountListCalls(cloud).Should().Be(6);
    }

    [Fact]
    public async Task MoveFile_invalidates_both_listings_when_a_folder_is_invalidated_during_rpc()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);
        var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.AfterResponse += (method, _) =>
        {
            if (method != FakeCloudMethods.MoveFileEntry)
                return;
            responseReady.TrySetResult();
            releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };

        var move = Task.Run(() => fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin",
            replace: false, new FakeDokanFileInfo()));
        try
        {
            await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gateway.InvalidateListing(sourceDirectoryId);
        }
        finally
        {
            releaseResponse.TrySetResult();
        }

        (await move.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(DokanResult.Success);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId);
        CountListCalls(cloud).Should().Be(5);
    }

    [Fact]
    public async Task MoveFile_does_not_apply_a_listing_update_after_account_epoch_changes()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);
        var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.AfterResponse += (method, _) =>
        {
            if (method != FakeCloudMethods.MoveFileEntry)
                return;
            responseReady.TrySetResult();
            releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };

        var move = Task.Run(() => fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin",
            replace: false, new FakeDokanFileInfo()));
        try
        {
            await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gateway.ClearCache();
        }
        finally
        {
            releaseResponse.TrySetResult();
        }

        (await move.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(DokanResult.Success);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId);
        CountListCalls(cloud).Should().Be(5);
    }

    [Fact]
    public void MoveFile_keeps_invalidation_for_rpc_errors_and_case_insensitive_conflicts()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        var target = cloud.AddFile(destinationDirectoryId, "ITEM.BIN", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);

        fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.FileExists);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.MoveFileEntry);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().ContainSingle();
        gateway.ListDirectory(destinationDirectoryId).Files.Should().ContainSingle();

        gateway.DeleteFileEntry(target.EntryId);
        cloud.AddFile(destinationDirectoryId, "other.bin", [3]);
        gateway.InvalidateListing(sourceDirectoryId);
        gateway.InvalidateListing(destinationDirectoryId);
        gateway.ListDirectory(sourceDirectoryId);
        gateway.ListDirectory(destinationDirectoryId);
        cloud.Invoker.LoseNextResponseAfterEffect(FakeCloudMethods.MoveFileEntry);

        fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().ContainSingle();
        gateway.ListDirectory(destinationDirectoryId).Files.Should().ContainSingle(file => file.Entry.Name == "other.bin");
    }

    [Fact]
    public void MoveFileEntryAndUpdateListings_invalidates_both_listings_when_destination_index_conflicts_by_case()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        var target = cloud.AddFile(destinationDirectoryId, "ITEM.BIN", [2]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);
        var warmListCalls = CountListCalls(cloud);

        gateway.DeleteFileEntry(target.EntryId);
        gateway.MoveFileEntryAndUpdateListings(file.EntryId, sourceDirectoryId, destinationDirectoryId);

        CountListCalls(cloud).Should().Be(warmListCalls);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        CountListCalls(cloud).Should().Be(warmListCalls + 1);
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId && entry.Entry.Name == "item.bin");
        CountListCalls(cloud).Should().Be(warmListCalls + 2);
    }

    [Fact]
    public void MoveFileEntryAndUpdateListings_invalidates_source_when_destination_snapshot_is_missing()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        gateway.ListDirectory(sourceDirectoryId);
        var warmListCalls = CountListCalls(cloud);

        gateway.MoveFileEntryAndUpdateListings(file.EntryId, sourceDirectoryId, destinationDirectoryId);

        CountListCalls(cloud).Should().Be(warmListCalls);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        CountListCalls(cloud).Should().Be(warmListCalls + 1);
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId);
        CountListCalls(cloud).Should().Be(warmListCalls + 2);
    }

    [Fact]
    public void MoveFile_retries_after_a_tombstoned_destination_conflict_and_invalidates_listings()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        WarmListings(gateway, sourceDirectoryId, destinationDirectoryId);
        var injectedConflict = false;
        var snapshotCopyCount = 0;
        gateway.ListingMoveSnapshotCopiedForTesting = (_, _) => snapshotCopyCount++;
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.MoveFileEntry || injectedConflict)
                return;

            injectedConflict = true;
            var target = cloud.AddFile(destinationDirectoryId, "item.bin", [2]);
            gateway.QueueDeleteFileEntry(target.EntryId, destinationDirectoryId);
        };

        fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);

        injectedConflict.Should().BeTrue();
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.MoveFileEntry).Should().Be(2);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries).Should().Be(1);
        snapshotCopyCount.Should().Be(0);
        var listCallsAfterMove = CountListCalls(cloud);
        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        CountListCalls(cloud).Should().Be(listCallsAfterMove + 1);
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId && entry.Entry.Name == "item.bin");
        CountListCalls(cloud).Should().Be(listCallsAfterMove + 2);
    }

    [Fact]
    public void MoveFile_cache_remains_correct_after_folder_enumeration_cleanup_and_close()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sourceDirectoryId = cloud.AddDirectory("Source");
        var destinationDirectoryId = cloud.AddDirectory("Destination");
        var file = cloud.AddFile(sourceDirectoryId, "item.bin", [1]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        fileSystem.FindFiles("\\Source", out var sourceFiles, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);
        fileSystem.FindFiles("\\Destination", out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);
        sourceFiles.Should().ContainSingle(item => item.FileName == "item.bin");

        var openInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\Source\\item.bin", FileAccess.ReadData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, openInfo).Should().Be(DokanResult.Success);
        var warmListCalls = CountListCalls(cloud);
        var snapshotCopyCount = 0;
        gateway.ListingMoveSnapshotCopiedForTesting = (_, _) => snapshotCopyCount++;
        fileSystem.MoveFile("\\Source\\item.bin", "\\Destination\\item.bin", replace: false,
            new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\Source\\item.bin", openInfo);
        fileSystem.CloseFile("\\Source\\item.bin", openInfo);
        snapshotCopyCount.Should().Be(1);
        CountListCalls(cloud).Should().Be(warmListCalls);

        gateway.ListDirectory(sourceDirectoryId).Files.Should().BeEmpty();
        gateway.ListDirectory(destinationDirectoryId).Files.Should()
            .ContainSingle(entry => entry.Entry.Id == file.EntryId);
        CountListCalls(cloud).Should().Be(warmListCalls);
        fileSystem.LastSyncError.Should().BeNull();
    }

    private static void WarmListings(CloudGateway gateway, string sourceDirectoryId, string destinationDirectoryId)
    {
        gateway.ListDirectory("");
        gateway.ListDirectory(sourceDirectoryId);
        gateway.ListDirectory(destinationDirectoryId);
    }

    private static int CountListCalls(FakeCloud cloud)
        => cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed);

    private static IReadOnlyDictionary<string, FileEntryDetailed> CachedFileIndex(
        CloudGateway gateway, string directoryId, string indexName)
    {
        var cache = (IDictionary)typeof(CloudGateway)
            .GetField("_listCache", System.Reflection.BindingFlags.Instance
                                       | System.Reflection.BindingFlags.NonPublic)!.GetValue(gateway)!;
        var cachedListing = cache[directoryId]!;
        var index = cachedListing.GetType().GetProperty("Index")!.GetValue(cachedListing)!;
        return (IReadOnlyDictionary<string, FileEntryDetailed>)index.GetType()
            .GetProperty(indexName)!.GetValue(index)!;
    }
}
