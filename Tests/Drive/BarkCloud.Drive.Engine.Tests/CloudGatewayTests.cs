using System.Net;
using System.Net.Http.Headers;

using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayTests
{
    [Fact]
    public void Resolve_finds_files_case_insensitively()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("Documents");
        var file = cloud.AddFile(directoryId, "report.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var resolved = gateway.Resolve("\\documents\\REPORT.txt");

        resolved.Should().NotBeNull();
        resolved!.EntryId.Should().Be(file.EntryId);
        resolved.FileId.Should().Be(file.FileId);
        resolved.DirectoryId.Should().Be(directoryId);
    }

    [Fact]
    public void Name_indexes_preserve_resolve_and_move_lookup_order()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var lowerCaseFile = cloud.AddFile("", "report.txt");
        cloud.AddFile("", "REPORT.TXT");
        var sameNameDirectory = cloud.AddDirectory("same.txt");
        cloud.AddFile("", "same.txt");
        var lowerCaseDirectory = cloud.AddDirectory("docs");
        cloud.AddDirectory("DOCS");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var listing = gateway.ListDirectory("");

        gateway.Resolve("\\report.txt")!.EntryId.Should().Be(listing.Files[0].Entry.Id);
        gateway.FindFileByName("", listing, "report.txt", exactFirst: true)!.Entry.Id
            .Should().Be(lowerCaseFile.EntryId);

        var sameName = gateway.Resolve("\\SAME.TXT");
        sameName.Should().NotBeNull();
        sameName!.IsDirectory.Should().BeTrue();
        sameName.DirectoryId.Should().Be(sameNameDirectory);

        gateway.FindSubdirectoryByName("", listing, "docs", exactFirst: true)!.Id
            .Should().Be(lowerCaseDirectory);
    }

    [Fact]
    public void Resolve_skips_a_tombstoned_name_match_for_a_live_case_variant()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var deletedFile = cloud.AddFile("", "report.txt");
        var liveFile = cloud.AddFile("", "REPORT.TXT");
        var unrelatedDeletedFile = cloud.AddFile("", "unrelated.txt");
        var visibleFile = cloud.AddFile("", "visible.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        gateway.QueueDeleteFileEntry(deletedFile.EntryId, "");
        gateway.QueueDeleteFileEntry(unrelatedDeletedFile.EntryId, "");

        gateway.Resolve("\\visible.txt")!.EntryId.Should().Be(visibleFile.EntryId);
        gateway.Resolve("\\report.txt")!.EntryId.Should().Be(liveFile.EntryId);
        gateway.ListDirectory("").Files.Select(file => file.Entry.Id)
            .Should().BeEquivalentTo(new[] { liveFile.EntryId, visibleFile.EntryId });
    }

    [Fact]
    public void ListDirectory_fetches_all_pages()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        for (var i = 0; i < 205; i++)
            cloud.AddFile("", $"file-{i:D3}.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var listing = gateway.ListDirectory("");

        listing.Files.Should().HaveCount(205);
        var calls = cloud.Invoker.Calls
            .Where(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .ToArray();
        calls.Should().HaveCount(2);
        calls.Select(call => ((ListDirectoryRequest)call.Request).Limit).Should().OnlyContain(limit => limit == 200);
        ((ListDirectoryRequest)calls[1].Request).CursorEntryId.Should().NotBeEmpty();
    }

    [Fact]
    public void ListDirectory_reuses_entries_until_ttl_expires()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "first.txt");
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        gateway.ListDirectory("").Files.Should().HaveCount(1);
        cloud.AddFile("", "second.txt");

        time.Advance(TimeSpan.FromSeconds(4));
        gateway.ListDirectory("").Files.Should().HaveCount(1);
        time.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
        gateway.ListDirectory("").Files.Should().HaveCount(2);

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public void Resolve_reuses_the_name_index_for_a_large_listing_within_ttl()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        for (var i = 0; i < 10_000; i++)
            cloud.AddFile("", $"file-{i:D5}.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        gateway.Resolve("\\FILE-09999.TXT")!.Name.Should().Be("file-09999.txt");
        gateway.Resolve("\\FILE-00000.TXT")!.Name.Should().Be("file-00000.txt");

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(50);
    }

    [Fact]
    public async Task Concurrent_cold_directory_requests_share_one_pagination()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        for (var i = 0; i < 205; i++)
            cloud.AddFile("", $"file-{i:D3}.txt");
        var callersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWaitersJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int callerCount = 20;
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var joinedWaiters = 0;
        gateway.ListingLoadWaiterJoinedForTesting = () =>
        {
            if (Interlocked.Increment(ref joinedWaiters) == callerCount - 1)
                allWaitersJoined.TrySetResult();
        };
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            requestStarted.TrySetResult();
            releaseRequest.Task.GetAwaiter().GetResult();
        };

        var readyCount = 0;
        var requests = Enumerable.Range(0, callerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            if (Interlocked.Increment(ref readyCount) == callerCount)
                callersReady.TrySetResult();
            startCallers.Task.GetAwaiter().GetResult();
            return gateway.ListDirectory("");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await callersReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        startCallers.TrySetResult();
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await allWaitersJoined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseRequest.TrySetResult();
        }

        var listings = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        listings.Should().OnlyContain(listing => listing.Files.Count == 205);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public async Task Canceling_a_waiter_does_not_cancel_the_shared_listing_load()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "shared.txt");
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiterCancellation = new CancellationTokenSource();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.ListingLoadWaiterJoinedForTesting = () => waiterJoined.TrySetResult();
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            requestStarted.TrySetResult();
            releaseRequest.Task.GetAwaiter().GetResult();
        };

        var owner = Task.Factory.StartNew(() => gateway.ListDirectory(""),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiter = Task.Factory.StartNew(() => gateway.ListDirectory("", waiterCancellation.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await waiterJoined.Task.WaitAsync(TimeSpan.FromSeconds(10));

            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiter.WaitAsync(TimeSpan.FromSeconds(10)));
            owner.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseRequest.TrySetResult();
        }

        (await owner.WaitAsync(TimeSpan.FromSeconds(10))).Files
            .Should().ContainSingle(file => file.Entry.Name == "shared.txt");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceling_the_listing_owner_retries_for_a_live_waiter(bool useRpcCancellation)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "retry.txt");
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var ownerCancellation = new CancellationTokenSource();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.ListingLoadWaiterJoinedForTesting = () => waiterJoined.TrySetResult();
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;

            var request = Interlocked.Increment(ref requestCount);
            if (request == 1)
            {
                requestStarted.TrySetResult();
                var cancellationToken = cloud.Invoker.Calls[^1].CancellationToken;
                cancellationToken.WaitHandle.WaitOne();
                if (useRpcCancellation)
                    throw new RpcException(new Status(StatusCode.Cancelled, "owner canceled"));
                throw new OperationCanceledException(cancellationToken);
            }

        };

        var owner = Task.Factory.StartNew(() => gateway.ListDirectory("", ownerCancellation.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiter = Task.Factory.StartNew(() => gateway.ListDirectory(""),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await waiterJoined.Task.WaitAsync(TimeSpan.FromSeconds(10));

        ownerCancellation.Cancel();
        Exception? ownerFailure = null;
        try
        {
            await owner.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (RpcException ex)
        {
            ownerFailure = ex;
        }
        catch (OperationCanceledException ex)
        {
            ownerFailure = ex;
        }

        ownerFailure.Should().NotBeNull();
        if (ownerFailure is RpcException rpcFailure)
            rpcFailure.StatusCode.Should().Be(StatusCode.Cancelled);
        else
            ownerFailure.Should().BeAssignableTo<OperationCanceledException>();
        (await waiter.WaitAsync(TimeSpan.FromSeconds(10))).Files
            .Should().ContainSingle(file => file.Entry.Name == "retry.txt");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public async Task InvalidateListing_during_a_load_starts_a_new_load_and_discards_the_old_result()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "old.txt");
        var firstResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseCount = 0;
        cloud.Invoker.AfterResponse += (method, _) =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            if (Interlocked.Increment(ref responseCount) == 1)
            {
                firstResponseReady.TrySetResult();
                releaseFirstResponse.Task.GetAwaiter().GetResult();
            }
            else
                secondResponseReady.TrySetResult();
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var firstRequest = Task.Factory.StartNew(() => gateway.ListDirectory(""),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await firstResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cloud.AddFile("", "new.txt");
            gateway.InvalidateListing("");

            var secondRequest = Task.Factory.StartNew(() => gateway.ListDirectory(""),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await secondResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var freshListing = await secondRequest.WaitAsync(TimeSpan.FromSeconds(10));
            freshListing.Files.Should().Contain(file => file.Entry.Name == "new.txt");
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
        }

        var oldListing = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        oldListing.Files.Should().NotContain(file => file.Entry.Name == "new.txt");
        gateway.ListDirectory("").Files.Should().Contain(file => file.Entry.Name == "new.txt");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public async Task ClearCache_during_a_load_uses_a_new_account_epoch()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "old.txt");
        var firstResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseCount = 0;
        cloud.Invoker.AfterResponse += (method, _) =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            if (Interlocked.Increment(ref responseCount) == 1)
            {
                firstResponseReady.TrySetResult();
                releaseFirstResponse.Task.GetAwaiter().GetResult();
            }
            else
                secondResponseReady.TrySetResult();
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var firstRequest = Task.Factory.StartNew(() => gateway.ListDirectory(""),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await firstResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cloud.AddFile("", "new.txt");
            gateway.ClearCache();

            var secondRequest = Task.Factory.StartNew(() => gateway.ListDirectory(""),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await secondResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var freshListing = await secondRequest.WaitAsync(TimeSpan.FromSeconds(10));
            freshListing.Files.Should().Contain(file => file.Entry.Name == "new.txt");
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
        }

        var oldListing = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        oldListing.Files.Should().NotContain(file => file.Entry.Name == "new.txt");
        gateway.ListDirectory("").Files.Should().Contain(file => file.Entry.Name == "new.txt");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_cold_directory_requests_share_rpc_failures_without_caching_them()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "retry.txt");
        var callersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.ListingLoadWaiterJoinedForTesting = () => waiterJoined.TrySetResult();
        cloud.Invoker.FailNext(FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.ListDirectoryDetailed)
                return;
            requestStarted.TrySetResult();
            releaseRequest.Task.GetAwaiter().GetResult();
        };

        const int callerCount = 2;
        var readyCount = 0;
        var requests = Enumerable.Range(0, callerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            if (Interlocked.Increment(ref readyCount) == callerCount)
                callersReady.TrySetResult();
            startCallers.Task.GetAwaiter().GetResult();
            return gateway.ListDirectory("");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await callersReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        startCallers.TrySetResult();
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await waiterJoined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseRequest.TrySetResult();
        }

        var failures = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                await request.WaitAsync(TimeSpan.FromSeconds(10));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }));
        failures.Select(error => (error as RpcException)?.StatusCode)
            .Should().OnlyContain(statusCode => statusCode == StatusCode.Unavailable);

        gateway.ListDirectory("").Files.Should().ContainSingle(file => file.Entry.Name == "retry.txt");
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public void Buffered_delete_hides_entries_and_sends_a_batch_of_100()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var files = Enumerable.Range(0, 100).Select(i => cloud.AddFile("", $"file-{i:D3}.txt")).ToArray();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        foreach (var file in files.Take(99))
            gateway.QueueDeleteFileEntry(file.EntryId, "");
        gateway.ListDirectory("").Files.Should().ContainSingle();

        gateway.QueueDeleteFileEntry(files[99].EntryId, "");

        var deleteCalls = cloud.Invoker.Calls
            .Where(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
            .ToArray();
        deleteCalls.Should().ContainSingle();
        ((DeleteFileEntriesRequest)deleteCalls[0].Request).EntryIds.Should().HaveCount(100);
        deleteCalls[0].Deadline.Should().Be(time.GetUtcNow().UtcDateTime.AddSeconds(5));
        gateway.ListDirectory("").Files.Should().BeEmpty();
    }

    [Fact]
    public void Buffered_delete_sends_a_batch_after_the_quiet_window()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var files = new[] { cloud.AddFile("", "first.txt"), cloud.AddFile("", "second.txt") };
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);

        foreach (var file in files)
            gateway.QueueDeleteFileEntry(file.EntryId, "");

        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries);
        time.Advance(TimeSpan.FromSeconds(1));

        var deleteCall = cloud.Invoker.Calls.Should()
            .ContainSingle(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
            .Subject;
        ((DeleteFileEntriesRequest)deleteCall.Request).EntryIds.Should().HaveCount(2);
    }

    [Fact]
    public void FlushPending_returns_hidden_entries_when_delete_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var file = cloud.AddFile("", "pending-delete.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        gateway.QueueDeleteFileEntry(file.EntryId, "");
        cloud.Invoker.FailNext(
            FakeCloudMethods.DeleteFileEntries,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        gateway.FlushPending();

        gateway.ListDirectory("").Files.Should().ContainSingle(item => item.Entry.Id == file.EntryId);
    }

    [Fact]
    public async Task FlushPending_throw_on_failure_processes_and_requeues_all_chunks()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var files = Enumerable.Range(0, 101).Select(i => cloud.AddFile("", $"file-{i:D3}.txt")).ToArray();
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        using var firstCallStarted = new ManualResetEventSlim();
        using var releaseFirstCall = new ManualResetEventSlim();
        var failDeletes = true;
        var callNumber = 0;
        cloud.Invoker.OnCall += method =>
        {
            if (method != FakeCloudMethods.DeleteFileEntries)
                return;

            if (Interlocked.Increment(ref callNumber) == 1)
            {
                firstCallStarted.Set();
                releaseFirstCall.Wait();
            }

            if (failDeletes)
                cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntries,
                    new RpcException(new Status(StatusCode.Unavailable, "offline")));
        };

        var queueFirstHundred = Task.Run(() =>
        {
            foreach (var file in files.Take(100))
                gateway.QueueDeleteFileEntry(file.EntryId, "");
        });

        try
        {
            firstCallStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            gateway.QueueDeleteFileEntry(files[100].EntryId, "");
            releaseFirstCall.Set();
            await queueFirstHundred.WaitAsync(TimeSpan.FromSeconds(5));

            Action flush = () => gateway.FlushPending(throwOnFailure: true);
            flush.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);

            var failedCalls = cloud.Invoker.Calls
                .Where(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
                .ToArray();
            failedCalls.Should().HaveCount(3);
            failedCalls.Select(call => ((DeleteFileEntriesRequest)call.Request).EntryIds.Count)
                .Should().Equal(100, 100, 1);
            gateway.ListDirectory("").Files.Should().BeEmpty();

            failDeletes = false;
            time.Advance(TimeSpan.FromSeconds(1));

            cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
                .Should().Be(5);
            files.Should().OnlyContain(file => cloud.IsInTrash(file.EntryId));
            gateway.ListDirectory("").Files.Should().BeEmpty();
        }
        finally
        {
            releaseFirstCall.Set();
            await queueFirstHundred;
        }
    }

    [Fact]
    public void Exhausted_buffered_delete_attempts_report_sync_error()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var file = cloud.AddFile("", "pending-delete.txt");
        var time = new FakeTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var errors = new List<string>();
        gateway.SyncError = errors.Add;
        for (var i = 0; i < 3; i++)
            cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntries,
                new RpcException(new Status(StatusCode.Unavailable, "offline")));
        gateway.QueueDeleteFileEntry(file.EntryId, "");

        Action flush = () => gateway.FlushPending(throwOnFailure: true);
        flush.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);
        gateway.ListDirectory("").Files.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(1));
        gateway.ListDirectory("").Files.Should().BeEmpty();
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));

        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries).Should().Be(3);
        errors.Should().ContainSingle().Which.Should().Contain(file.EntryId);
        gateway.ListDirectory("").Files.Should().ContainSingle(item => item.Entry.Id == file.EntryId);
    }

    [Fact]
    public void DeleteDirectory_does_not_call_rpc_when_strict_file_flush_fails()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("directory");
        var file = cloud.AddFile(directoryId, "pending-delete.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        gateway.QueueDeleteFileEntry(file.EntryId, directoryId);
        cloud.Invoker.FailNext(FakeCloudMethods.DeleteFileEntries,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        Action delete = () => gateway.DeleteDirectory(directoryId);

        delete.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);
        cloud.Invoker.Calls.Should().NotContain(call => call.MethodFullName == FakeCloudMethods.DeleteDirectory);
    }

    [Fact]
    public void Resolve_returns_root_directories_and_null_for_missing_paths()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var directoryId = cloud.AddDirectory("Documents");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var root = gateway.Resolve("\\");
        root.Should().NotBeNull();
        root!.IsDirectory.Should().BeTrue();
        root.DirectoryId.Should().BeEmpty();

        var directory = gateway.Resolve("\\Documents");
        directory.Should().NotBeNull();
        directory!.IsDirectory.Should().BeTrue();
        directory.DirectoryId.Should().Be(directoryId);
        gateway.Resolve("\\missing").Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_when_a_file_is_in_the_middle_of_the_path()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "file.txt");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        gateway.Resolve("\\file.txt\\child.txt").Should().BeNull();
    }

    [Fact]
    public void Resolve_and_ListDirectory_propagate_listing_rpc_errors()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        cloud.Invoker.FailNext(
            FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));

        var resolve = () => gateway.Resolve("\\missing");

        resolve.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);

        cloud.Invoker.FailNext(
            FakeCloudMethods.ListDirectoryDetailed,
            new RpcException(new Status(StatusCode.Unavailable, "offline")));
        var list = () => gateway.ListDirectory("");

        list.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public void FakeCloud_clamps_directory_listing_limit_to_the_backend_maximum()
    {
        var cloud = new FakeCloud();
        for (var i = 0; i < 205; i++)
            cloud.AddFile("", $"file-{i:D3}.txt");
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        var page = client.ListDirectoryDetailed(new ListDirectoryRequest { Limit = 500 });

        page.Files.Should().HaveCount(200);
        page.NextCursorEntryId.Should().NotBeEmpty();
    }

    [Fact]
    public void FakeCloud_uses_separate_case_sensitive_file_and_directory_names()
    {
        var cloud = new FakeCloud();
        var client = new CloudApi.CloudApiClient(cloud.Invoker);
        var file = cloud.AddFile("", "item.txt");
        var directory = cloud.AddDirectory("item.txt");
        cloud.AddFile("", "ITEM.txt");
        cloud.AddDirectory("ITEM.txt");

        client.RenameFileEntry(new RenameFileEntryRequest { EntryId = file.EntryId, NewName = "Item.txt" });
        client.RenameDirectory(new RenameDirectoryRequest { DirectoryId = directory, NewName = "Item.txt" });
    }

    [Fact]
    public void FakeCloud_attach_and_restore_suffix_only_for_file_name_conflicts()
    {
        var cloud = new FakeCloud();
        cloud.AddDirectory("same.txt");
        var fileId = cloud.AddUnattachedFile();
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        client.AttachFile(new AttachFileRequest { DirectoryId = "", FileId = fileId, Name = "same.txt" });
        var attached = client.ListDirectoryDetailed(new ListDirectoryRequest { DirectoryId = "", Limit = 50 })
            .Files.Should().ContainSingle().Which;
        attached.Entry.Name.Should().Be("same.txt");

        client.DeleteFileEntry(new DeleteFileEntryRequest { EntryId = attached.Entry.Id });
        client.RestoreFromTrash(new RestoreFromTrashRequest { EntryId = attached.Entry.Id });

        client.ListDirectoryDetailed(new ListDirectoryRequest { DirectoryId = "", Limit = 50 })
            .Files.Should().ContainSingle().Which.Entry.Name.Should().Be("same.txt");
    }

    [Fact]
    public void FakeCloud_reports_circular_directory_moves_with_the_backend_error_code()
    {
        var cloud = new FakeCloud();
        var parent = cloud.AddDirectory("parent");
        var child = cloud.AddDirectory("child", parent);
        var client = new CloudApi.CloudApiClient(cloud.Invoker);

        var exception = Assert.Throws<RpcException>(() => client.MoveDirectory(
            new MoveDirectoryRequest { DirectoryId = parent, NewParentId = child }));

        exception.Status.Detail.Should().Be("Операция отклонена сервером");
        exception.Trailers.GetValue("x-error-code").Should().Be("F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001");
    }

    [Fact]
    public async Task CloudGateway_uploads_content_and_reads_ranges()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var content = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var uploadPath = temp.Path("upload.bin");
        File.WriteAllBytes(uploadPath, content);
        var operation = new PersistOperation();

        var fileId = await gateway.UploadResumableAsync(operation, uploadPath, "upload.bin", () => { },
            TimeSpan.FromMinutes(2));
        gateway.AttachFile("", fileId, "upload.bin", uploadSessionId: operation.Upload2!.SessionId);
        var buffer = new byte[4];
        var bytesRead = gateway.Read(fileId, content.Length, buffer, 2);

        fileId.Should().Be(operation.FileId);
        bytesRead.Should().Be(4);
        buffer.Should().Equal(content[2..6]);
        cloud.HttpHandler.Calls.Should().HaveCount(4);
        cloud.HttpHandler.Calls.Take(3).Should().OnlyContain(call => call.Method == "PUT"
            && call.Headers.ContainsKey("X-Upload-Token"));
        cloud.HttpHandler.Calls.Should().NotContain(call => call.Method == "POST");
        cloud.HttpHandler.Calls[3].Range.Should().Be("bytes=0-7");
    }

    [Fact]
    public void Read_caches_a_valid_partial_response()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var file = cloud.AddFile("", "cached.bin", content);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var first = new byte[4];
        var second = new byte[3];

        gateway.Read(file.FileId, content.Length, first, 0).Should().Be(4);
        gateway.Read(file.FileId, content.Length, second, 3).Should().Be(3);

        first.Should().Equal(content[..4]);
        second.Should().Equal(content[3..6]);
        cloud.HttpHandler.Calls.Should().ContainSingle();
        cloud.HttpHandler.Calls[0].Range.Should().Be("bytes=0-7");
        File.Exists(Path.Combine(temp.Path("cache"), file.FileId + ".blocks", "0.blk")).Should().BeTrue();
    }

    [Fact]
    public void Read_uses_whole_download_only_after_http_200()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var file = cloud.AddFile("", "whole.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var buffer = new byte[4];

        gateway.Read(file.FileId, content.Length, buffer, 2).Should().Be(4);

        buffer.Should().Equal(content[2..6]);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        cloud.HttpHandler.Calls[0].Range.Should().Be("bytes=0-7");
        cloud.HttpHandler.Calls[1].Range.Should().BeNull();
        File.Exists(Path.Combine(temp.Path("cache"), file.FileId)).Should().BeTrue();
    }

    [Fact]
    public void Read_does_not_use_whole_download_after_http_500()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var file = cloud.AddFile("", "error.bin", [1, 2, 3]);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.InternalServerError, []));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var buffer = new byte[3];

        var read = () => gateway.Read(file.FileId, 3, buffer, 0);

        read.Should().Throw<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        cloud.HttpHandler.Calls.Should().ContainSingle();
        cloud.HttpHandler.Calls[0].Range.Should().Be("bytes=0-2");
    }

    [Fact]
    public void Read_refreshes_temp_url_once_after_http_403()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "refresh.bin", content);
        var defaultResponder = cloud.HttpHandler.Responder;
        var attempts = 0;
        cloud.HttpHandler.Responder = (request, cancellationToken) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromResult(Response(HttpStatusCode.Forbidden, []))
                : defaultResponder(request, cancellationToken);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var buffer = new byte[4];

        gateway.Read(file.FileId, content.Length, buffer, 0).Should().Be(4);

        buffer.Should().Equal(content);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.GetTempDownloadUrl).Should().Be(2);
    }

    [Fact]
    public void Read_throws_after_two_forbidden_responses()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "forbidden.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden, []));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var read = () => gateway.Read(file.FileId, content.Length, new byte[content.Length], 0);

        read.Should().Throw<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.GetTempDownloadUrl).Should().Be(2);
    }

    [Fact]
    public void Read_does_not_publish_a_block_with_invalid_content_range()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "bad-range.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(PartialResponse(content, 1, 3, content.Length));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var read = () => gateway.Read(file.FileId, content.Length, new byte[4], 0);

        read.Should().Throw<InvalidDataException>();
        Directory.Exists(Path.Combine(temp.Path("cache"), file.FileId + ".blocks")).Should().BeFalse();
    }

    [Fact]
    public void Read_does_not_publish_a_block_with_an_invalid_content_range_total()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "wrong-total.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(PartialResponse(content, 0, 3, content.Length + 1));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var read = () => gateway.Read(file.FileId, content.Length, new byte[content.Length], 0);

        read.Should().Throw<InvalidDataException>();
        Directory.Exists(Path.Combine(temp.Path("cache"), file.FileId + ".blocks")).Should().BeFalse();
    }

    [Fact]
    public void Read_does_not_publish_a_block_without_content_range()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "missing-range.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.PartialContent, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var read = () => gateway.Read(file.FileId, content.Length, new byte[content.Length], 0);

        read.Should().Throw<InvalidDataException>();
        Directory.Exists(Path.Combine(temp.Path("cache"), file.FileId + ".blocks")).Should().BeFalse();
    }

    [Fact]
    public void Read_does_not_publish_a_block_with_a_short_body()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "short-range.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(PartialResponse([1, 2, 3], 0, 3, content.Length));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var read = () => gateway.Read(file.FileId, content.Length, new byte[4], 0);

        read.Should().Throw<InvalidDataException>();
        Directory.GetFiles(Path.Combine(temp.Path("cache"), file.FileId + ".blocks")).Should().BeEmpty();
    }

    [Fact]
    public void Failed_whole_download_is_removed_from_task_cache_and_retried()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "retry-whole.bin", content);
        var attempts = 0;
        cloud.HttpHandler.Responder = (_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(Response(HttpStatusCode.OK, attempt == 2 ? [1, 2, 3] : content));
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var buffer = new byte[4];

        var firstRead = () => gateway.Read(file.FileId, content.Length, buffer, 0);
        firstRead.Should().Throw<InvalidDataException>();
        gateway.Read(file.FileId, content.Length, buffer, 0).Should().Be(4);

        buffer.Should().Equal(content);
        cloud.HttpHandler.Calls.Should().HaveCount(3);
        cloud.HttpHandler.Calls[0].Range.Should().Be("bytes=0-3");
        cloud.HttpHandler.Calls[1].Range.Should().BeNull();
        cloud.HttpHandler.Calls[2].Range.Should().BeNull();
        Directory.GetFiles(temp.Path("cache"), "*.part").Should().BeEmpty();
    }

    [Fact]
    public void Read_crosses_into_a_nonzero_incomplete_last_block()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        const int blockSize = 1 << 20;
        var content = Enumerable.Range(0, blockSize + 17).Select(i => (byte)(i % 251)).ToArray();
        var file = cloud.AddFile("", "multi-block.bin", content);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var offset = blockSize - 3;
        var buffer = new byte[12];

        gateway.Read(file.FileId, content.Length, buffer, offset).Should().Be(buffer.Length);

        buffer.Should().Equal(content.AsSpan((int)offset, buffer.Length).ToArray());
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        cloud.HttpHandler.Calls[0].Range.Should().Be($"bytes=0-{blockSize - 1}");
        cloud.HttpHandler.Calls[1].Range.Should().Be($"bytes={blockSize}-{content.Length - 1}");

        var blockPath = Path.Combine(temp.Path("cache"), file.FileId + ".blocks", "1.blk");
        new FileInfo(blockPath).Length.Should().Be(17);
    }

    [Fact]
    public async Task Concurrent_reads_of_one_block_share_one_http_request()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "concurrent.bin", content);
        var allReadsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedReads = 0;
        cloud.HttpHandler.Responder = async (_, cancellationToken) =>
        {
            responseStarted.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return PartialResponse(content, 0, content.Length - 1, content.Length);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));

        var reads = Enumerable.Range(0, 10).Select(_ => Task.Factory.StartNew(() =>
        {
            if (Interlocked.Increment(ref startedReads) == 10)
                allReadsStarted.TrySetResult();
            var buffer = new byte[4];
            var bytesRead = gateway.Read(file.FileId, content.Length, buffer, 0);
            return (bytesRead, buffer);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        int callsWhileResponseHeld;
        try
        {
            await allReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            callsWhileResponseHeld = cloud.HttpHandler.Calls.Count;
        }
        finally
        {
            releaseResponse.TrySetResult();
        }
        var results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10));

        callsWhileResponseHeld.Should().Be(1);
        results.Should().OnlyContain(result => result.bytesRead == content.Length && result.buffer.SequenceEqual(content));
        cloud.HttpHandler.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task DownloadTo_checks_expected_length_and_removes_part_on_mismatch()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var file = cloud.AddFile("", "hydrate.bin", [1, 2, 3]);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var destination = temp.Path("hydrated.bin");

        var download = () => gateway.DownloadToAsync(file.FileId, destination, 4);

        await download.Should().ThrowAsync<InvalidDataException>();
        File.Exists(destination).Should().BeFalse();
        Directory.GetFiles(temp.Root, "*.part").Should().BeEmpty();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body)
        => new(status) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage PartialResponse(byte[] body, long from, long to, long total)
    {
        var response = Response(HttpStatusCode.PartialContent, body);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return response;
    }
}
