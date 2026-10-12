using System.Net;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using DokanNet;

using FluentAssertions;

using Microsoft.Extensions.Time.Testing;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class FileSystemTimeoutTests
{
    [Fact]
    public void Dokan_keep_alive_stops_ticking_after_dispose()
    {
        var time = new FakeTimeProvider();
        var info = new FakeDokanFileInfo();
        var keepAlive = DokanKeepAlive.Start(info, time);

        keepAlive.Dispose();
        time.Advance(TimeSpan.FromSeconds(30));

        info.TryResetTimeoutCalls.Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_renews_Dokany_timeout_during_persist()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var originalResponder = cloud.HttpHandler.Responder;
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
            }
            return await originalResponder(request, cancellationToken);
        };

        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\keepalive.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.CreateNew, FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\keepalive.bin", [1], out _, 0, info).Should().Be(DokanResult.Success);

        var cleanup = Task.Run(() => fileSystem.Cleanup("\\keepalive.bin", info));
        try
        {
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));

            info.TryResetTimeoutCalls.Should().Be(1);
            info.LastTimeoutMilliseconds.Should().Be(60_000);
        }
        finally
        {
            releaseUpload.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            fileSystem.CloseFile("\\keepalive.bin", info);
        }
    }

    [Fact]
    public async Task CloseFile_renews_Dokany_timeout_during_persist()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var originalResponder = cloud.HttpHandler.Responder;
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var info = OpenAndWrite(fileSystem, "\\close-keepalive.bin", [1]);
        fileSystem.Cleanup("\\close-keepalive.bin", info);
        fileSystem.WriteFile("\\close-keepalive.bin", [2], out _, 1, info).Should().Be(DokanResult.Success);
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
            }
            return await originalResponder(request, cancellationToken);
        };

        var close = Task.Run(() => fileSystem.CloseFile("\\close-keepalive.bin", info));
        try
        {
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));

            info.TryResetTimeoutCalls.Should().Be(1);
            info.LastTimeoutMilliseconds.Should().Be(60_000);
        }
        finally
        {
            releaseUpload.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Upload_timeout_uses_localized_message_in_LastSyncError()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var originalResponder = cloud.HttpHandler.Responder;
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                uploadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await originalResponder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var info = OpenAndWrite(fileSystem, "\\timeout.bin", [1]);

        var cleanup = Task.Run(() => fileSystem.Cleanup("\\timeout.bin", info));
        try
        {
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(60));
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

            fileSystem.LastSyncError.Should().Contain(Loc.T("Eng_HttpTransferTimedOut"));
        }
        finally
        {
            cloud.HttpHandler.Responder = originalResponder;
            fileSystem.CloseFile("\\timeout.bin", info);
        }
    }

    [Fact]
    public async Task Hydration_renews_Dokany_timeout()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "hydrate-keepalive.bin", [1, 2, 3]);
        var time = new FakeTimeProvider();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (_, cancellationToken) =>
        {
            downloadStarted.TrySetResult();
            await releaseDownload.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\hydrate-keepalive.bin", FileAccess.WriteData,
            FileShare.ReadWrite, FileMode.Open, FileOptions.None, FileAttributes.Normal, info)
            .Should().Be(DokanResult.Success);
        var buffer = new byte[3];
        var read = Task.Run(() => fileSystem.ReadFile("\\hydrate-keepalive.bin", buffer,
            out _, 0, info));
        NtStatus result;
        try
        {
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));

            info.TryResetTimeoutCalls.Should().Be(1);
            info.LastTimeoutMilliseconds.Should().Be(60_000);
        }
        finally
        {
            releaseDownload.TrySetResult();
            result = await read.WaitAsync(TimeSpan.FromSeconds(5));
            fileSystem.Cleanup("\\hydrate-keepalive.bin", info);
            fileSystem.CloseFile("\\hydrate-keepalive.bin", info);
        }

        result.Should().Be(DokanResult.Success);
        buffer.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task ReadFile_waiting_for_hydration_renews_its_own_Dokany_timeout()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "waiter.bin", [1, 2, 3]);
        var time = new TrackingTimeProvider();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalResponder = cloud.HttpHandler.Responder;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                downloadStarted.TrySetResult();
                await releaseDownload.Task.WaitAsync(cancellationToken);
            }
            return await originalResponder(request, cancellationToken);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var firstInfo = new FakeDokanFileInfo();
        var secondInfo = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\waiter.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, firstInfo).Should().Be(DokanResult.Success);
        fileSystem.CreateFile("\\waiter.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.Open,
            FileOptions.None, FileAttributes.Normal, secondInfo).Should().Be(DokanResult.Success);

        Task<(NtStatus Status, int BytesRead)> ReadAsync(FakeDokanFileInfo info)
            => Task.Run(() =>
            {
                var buffer = new byte[3];
                var status = fileSystem.ReadFile("\\waiter.bin", buffer, out var bytesRead, 0, info);
                return (status, bytesRead);
            });

        var firstRead = ReadAsync(firstInfo);
        Task<(NtStatus Status, int BytesRead)>? secondRead = null;
        try
        {
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondRead = ReadAsync(secondInfo);
            await time.WaitForKeepAliveTimerAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));

            secondInfo.TryResetTimeoutCalls.Should().BeGreaterThan(0);
            secondInfo.LastTimeoutMilliseconds.Should().Be(60_000);
        }
        finally
        {
            releaseDownload.TrySetResult();
            await firstRead.WaitAsync(TimeSpan.FromSeconds(5));
            if (secondRead is not null)
                await secondRead.WaitAsync(TimeSpan.FromSeconds(5));
            fileSystem.Cleanup("\\waiter.bin", firstInfo);
            fileSystem.Cleanup("\\waiter.bin", secondInfo);
            fileSystem.CloseFile("\\waiter.bin", firstInfo);
            fileSystem.CloseFile("\\waiter.bin", secondInfo);
        }
    }

    [Fact]
    public async Task ReadFile_starts_keep_alive_only_for_a_pending_download()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "cached.bin", [1]);
        cloud.AddFile("", "remote.bin", [2]);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1]),
        });
        var time = new TrackingTimeProvider();
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var buffer = new byte[1];
        var cachedInfo = new FakeDokanFileInfo();
        fileSystem.ReadFile("\\cached.bin", buffer, out var cachedBytes, 0, cachedInfo)
            .Should().Be(DokanResult.Success);
        cachedBytes.Should().Be(1);
        var keepAliveTimerCount = time.KeepAliveTimerCount;
        var cachedHitInfo = new FakeDokanFileInfo();
        fileSystem.ReadFile("\\cached.bin", buffer, out cachedBytes, 0, cachedHitInfo)
            .Should().Be(DokanResult.Success);
        cachedBytes.Should().Be(1);
        time.KeepAliveTimerCount.Should().Be(keepAliveTimerCount);

        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.HttpHandler.Responder = async (_, cancellationToken) =>
        {
            downloadStarted.TrySetResult();
            await releaseDownload.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
        };
        var remoteInfo = new FakeDokanFileInfo();
        var remoteKeepAliveTimerCount = time.KeepAliveTimerCount;
        var remoteRead = Task.Run(() =>
        {
            var readBuffer = new byte[1];
            var result = fileSystem.ReadFile("\\remote.bin", readBuffer, out var bytesRead, 0, remoteInfo);
            return (Result: result, BytesRead: bytesRead);
        });
        try
        {
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await time.WaitForKeepAliveTimerAsync(remoteKeepAliveTimerCount + 1)
                .WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));

            remoteInfo.TryResetTimeoutCalls.Should().Be(1);
        }
        finally
        {
            releaseDownload.TrySetResult();
            await remoteRead.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var remoteResult = await remoteRead;
        remoteResult.Result.Should().Be(DokanResult.Success);
        remoteResult.BytesRead.Should().Be(1);
    }

    [Fact]
    public async Task ReadFile_keeps_one_Dokany_timeout_alive_across_multiple_blocks()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "multi.bin", new byte[3 * (1 << 20)]);
        var originalResponder = cloud.HttpHandler.Responder;
        var time = new TrackingTimeProvider();
        var blockStarted = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var releaseBlock = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var blockIndex = 0;
        cloud.HttpHandler.Responder = async (request, cancellationToken) =>
        {
            var index = Interlocked.Increment(ref blockIndex) - 1;
            blockStarted[index].TrySetResult();
            await releaseBlock[index].Task.WaitAsync(cancellationToken);
            return await originalResponder(request, cancellationToken);
        };

        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), time: time);
        var info = new FakeDokanFileInfo();
        var read = Task.Run(() =>
        {
            var buffer = new byte[3 * (1 << 20)];
            var result = fileSystem.ReadFile("\\multi.bin", buffer, out var bytesRead, 0, info);
            return (Result: result, BytesRead: bytesRead);
        });

        try
        {
            await blockStarted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            await time.WaitForKeepAliveTimerAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(6));
            releaseBlock[0].TrySetResult();

            await blockStarted[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(6));
            info.TryResetTimeoutCalls.Should().Be(1);
            releaseBlock[1].TrySetResult();

            await blockStarted[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(6));
            info.TryResetTimeoutCalls.Should().Be(1);
            releaseBlock[2].TrySetResult();

            var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
            result.Result.Should().Be(DokanResult.Success);
            result.BytesRead.Should().Be(3 * (1 << 20));
        }
        finally
        {
            foreach (var release in releaseBlock)
                release.TrySetResult();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void SetAllocationSize_marks_a_shrunk_file_modified()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = OpenAndWrite(fileSystem, "\\shrink.bin", [1, 2, 3]);
        var file = ((HandleContext)info.Context!).File!;
        fileSystem.Cleanup("\\shrink.bin", info);
        file.Modified.Should().BeFalse();

        fileSystem.SetAllocationSize("\\shrink.bin", 2, info).Should().Be(DokanResult.Success);

        file.Modified.Should().BeTrue();
        fileSystem.CloseFile("\\shrink.bin", info);
    }

    [Fact]
    public void Write_after_persist_sets_DirtyAfterPersist()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));
        var info = OpenAndWrite(fileSystem, "\\paging.bin", [1]);
        var file = ((HandleContext)info.Context!).File!;

        fileSystem.Cleanup("\\paging.bin", info);
        file.Op!.Stage.Should().Be(PersistStage.Done);
        fileSystem.WriteFile("\\paging.bin", [2], out _, 1, info).Should().Be(DokanResult.Success);

        file.DirtyAfterPersist.Should().BeTrue();
        fileSystem.CloseFile("\\paging.bin", info);
    }

    [Fact]
    public void Failed_hydration_keeps_working_copy_unhydrated_and_removes_partial_download()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.AddFile("", "hydrate.bin", [1, 2, 3]);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1]),
        });
        var writeDir = temp.Path("write");
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, writeDir);

        var info = new FakeDokanFileInfo();
        var result = fileSystem.CreateFile("\\hydrate.bin", FileAccess.WriteData, FileShare.ReadWrite,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);
        result.Should().Be(DokanResult.Success);
        var file = ((HandleContext)info.Context!).File!;

        result = fileSystem.WriteFile("\\hydrate.bin", [4], out _, 1, info);
        result.Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        file.Hydrated.Should().BeFalse();
        file.Stream.Should().BeNull();
        Directory.GetFiles(writeDir).Should().BeEmpty();
        fileSystem.Cleanup("\\hydrate.bin", info);
        fileSystem.CloseFile("\\hydrate.bin", info);
    }

    [Theory]
    [InlineData(100L, 40L, 10L, 50L, 100L)]
    [InlineData(0L, 900L, 800L, 700L, 1000L)]
    [InlineData(100L, 90L, 20L, 0L, 100L)]
    public void GetDiskFreeSpace_uses_quota_or_physical_capacity(long limit, long used, long reserved,
        long expectedFree, long expectedTotal)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        cloud.Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ => new GetUserStorageInfoResponse
            {
                StorageLimit = limit,
                TotalUsedStorage = used,
                ReservedStorage = reserved,
                TotalAvailableStorage = 1000,
                DiskUsedStorage = 100,
                S3UsedStorage = 200,
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"));

        fileSystem.GetDiskFreeSpace(out var free, out var total, out var totalFree, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        free.Should().Be(expectedFree);
        totalFree.Should().Be(expectedFree);
        total.Should().Be(expectedTotal);
    }

    [Fact]
    public void Transfer_timeout_is_a_network_error_and_uncertain_mutation()
    {
        var error = new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"));

        BarkCloudFileSystem.ToStatus(error).Should().Be(BarkCloudFileSystem.UnexpectedNetworkError);
        BarkCloudFileSystem.IsUncertainAttachResult(error).Should().BeTrue();
        BarkCloudFileSystem.IsUncertainMoveResult(error).Should().BeTrue();
    }

    private static FakeDokanFileInfo OpenAndWrite(BarkCloudFileSystem fileSystem, string path, byte[] data)
    {
        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile(path, FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.WriteFile(path, data, out _, 0, info).Should().Be(DokanResult.Success);
        return info;
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
        private readonly FakeTimeProvider _inner = new();
        private readonly object _lock = new();
        private readonly Dictionary<int, TaskCompletionSource> _keepAliveTimerWaiters = new();
        private int _keepAliveTimerCount;

        public int KeepAliveTimerCount
        {
            get { lock (_lock) return _keepAliveTimerCount; }
        }

        public Task WaitForKeepAliveTimerAsync(int count)
        {
            lock (_lock)
            {
                if (_keepAliveTimerCount >= count)
                    return Task.CompletedTask;
                if (!_keepAliveTimerWaiters.TryGetValue(count, out var waiter))
                    _keepAliveTimerWaiters.Add(count,
                        waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                return waiter.Task;
            }
        }

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == KeepAliveInterval && period == KeepAliveInterval)
            {
                TaskCompletionSource? waiter;
                lock (_lock)
                {
                    _keepAliveTimerCount++;
                    _keepAliveTimerWaiters.Remove(_keepAliveTimerCount, out waiter);
                }
                waiter?.TrySetResult();
            }

            return timer;
        }

        public void Advance(TimeSpan amount) => _inner.Advance(amount);
    }
}

