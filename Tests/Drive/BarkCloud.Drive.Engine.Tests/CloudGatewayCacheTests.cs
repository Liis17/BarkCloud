using System.Diagnostics;
using System.Net;

using BarkCloud.Drive.Engine.Tests.Fakes;

using FluentAssertions;

using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayCacheTests
{
    [Fact]
    public async Task Cache_cleanup_evicts_oldest_entries_until_below_eighty_percent()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 10);
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var cache = temp.Path("cache");
        var oldest = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        var newer = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(oldest, new byte[6]);
        File.WriteAllBytes(newer, new byte[6]);
        File.SetLastWriteTimeUtc(oldest, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, time.GetUtcNow().UtcDateTime.AddHours(-1));

        time.Advance(TimeSpan.FromMinutes(1));
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(oldest).Should().BeFalse();
        File.Exists(newer).Should().BeTrue();
        new FileInfo(newer).Length.Should().BeLessThanOrEqualTo(8);
    }

    [Fact]
    public async Task Cache_cleanup_counts_and_evicts_individual_blocks()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 16);
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var cache = temp.Path("cache");
        var blocksDirectory = Path.Combine(cache, Guid.NewGuid().ToString("D") + ".blocks");
        var oldestBlock = Path.Combine(blocksDirectory, "0.blk");
        var noncanonicalBlock = Path.Combine(blocksDirectory, "00.blk");
        var part = Path.Combine(blocksDirectory, $"0.blk.{Guid.NewGuid():N}.part");
        var newer = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(blocksDirectory);
        File.WriteAllBytes(oldestBlock, new byte[6]);
        File.WriteAllBytes(noncanonicalBlock, new byte[6]);
        File.WriteAllBytes(part, new byte[6]);
        File.WriteAllBytes(newer, new byte[6]);
        File.SetLastWriteTimeUtc(part, time.GetUtcNow().UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(noncanonicalBlock, time.GetUtcNow().UtcDateTime.AddHours(-4));
        File.SetLastWriteTimeUtc(oldestBlock, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, time.GetUtcNow().UtcDateTime.AddHours(-1));

        time.Advance(TimeSpan.FromMinutes(1));
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(oldestBlock).Should().BeFalse();
        File.Exists(noncanonicalBlock).Should().BeTrue();
        File.Exists(part).Should().BeTrue();
        Directory.Exists(blocksDirectory).Should().BeTrue();
        File.Exists(newer).Should().BeTrue();
    }

    [Fact]
    public async Task Cache_cleanup_skips_an_open_file_and_continues()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var content = new byte[] { 1, 2, 3, 4, 5, 6 };
        var file = cloud.AddFile("", "open.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 10);
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        gateway.Read(file.FileId, content.Length, new byte[content.Length], 0).Should().Be(content.Length);
        var cachedPath = Path.Combine(temp.Path("cache"), file.FileId);
        var otherPath = Path.Combine(temp.Path("cache"), Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(otherPath, new byte[6]);
        File.SetLastWriteTimeUtc(cachedPath, time.GetUtcNow().UtcDateTime.AddSeconds(-29));
        File.SetLastWriteTimeUtc(otherPath, time.GetUtcNow().UtcDateTime.AddSeconds(-10));

        var deleteAttempts = 0;
        gateway.CacheFileBeforeDeleteForTesting = path =>
        {
            if (path == cachedPath)
                Interlocked.Increment(ref deleteAttempts);
        };

        gateway.CacheFileOpenedForTesting = path =>
        {
            if (path != cachedPath)
                return;

            time.Advance(TimeSpan.FromSeconds(61));
            gateway.WaitForCacheCleanupForTestingAsync().GetAwaiter().GetResult();
        };

        var readBuffer = new byte[content.Length];
        gateway.Read(file.FileId, content.Length, readBuffer, 0).Should().Be(content.Length);

        readBuffer.Should().Equal(content);
        deleteAttempts.Should().Be(1);
        File.Exists(cachedPath).Should().BeTrue();
        File.Exists(otherPath).Should().BeFalse();
    }

    [Fact]
    public void Read_recreates_a_removed_blocks_directory_before_creating_part()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4, 5, 6 };
        var file = cloud.AddFile("", "block-directory-race.bin", content);
        cloud.HttpHandler.Responder = (_, _) =>
        {
            var response = Response(HttpStatusCode.PartialContent, content);
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                0, content.Length - 1, content.Length);
            return Task.FromResult(response);
        };
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var removed = 0;
        gateway.BlockDirectoryCreatedForTesting = directory =>
        {
            if (Interlocked.Exchange(ref removed, 1) == 0)
                Directory.Delete(directory);
        };
        var buffer = new byte[content.Length];

        gateway.Read(file.FileId, content.Length, buffer, 0).Should().Be(content.Length);

        removed.Should().Be(1);
        buffer.Should().Equal(content);
        File.Exists(Path.Combine(temp.Path("cache"), file.FileId + ".blocks", "0.blk")).Should().BeTrue();
        cloud.HttpHandler.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void Read_redownloads_a_block_evicted_between_ensure_and_open_once()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "block-race.bin", content);
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var first = new byte[content.Length];
        gateway.Read(file.FileId, content.Length, first, 0).Should().Be(content.Length);

        var deleted = 0;
        gateway.CacheEntryBeforeOpenForTesting = path =>
        {
            if (Interlocked.Exchange(ref deleted, 1) == 0)
                File.Delete(path);
        };
        var second = new byte[content.Length];

        gateway.Read(file.FileId, content.Length, second, 0).Should().Be(content.Length);

        second.Should().Equal(content);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public void Read_redownloads_a_whole_file_evicted_between_ensure_and_open_once()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "whole-race.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var first = new byte[content.Length];
        gateway.Read(file.FileId, content.Length, first, 0).Should().Be(content.Length);

        var deleted = 0;
        gateway.CacheEntryBeforeOpenForTesting = path =>
        {
            if (Interlocked.Exchange(ref deleted, 1) == 0)
                File.Delete(path);
        };
        var second = new byte[content.Length];

        gateway.Read(file.FileId, content.Length, second, 0).Should().Be(content.Length);

        second.Should().Equal(content);
        cloud.HttpHandler.Calls.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_retries_once_after_unauthorized_cache_open(bool wholeFile)
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var content = new byte[] { 1, 2, 3, 4 };
        var file = cloud.AddFile("", "unauthorized-open.bin", content);
        if (wholeFile)
        {
            cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        }
        else
        {
            cloud.HttpHandler.Responder = (_, _) =>
            {
                var response = Response(HttpStatusCode.PartialContent, content);
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(0, content.Length - 1, content.Length);
                return Task.FromResult(response);
            };
        }

        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var beforeOpen = 0;
        var opened = 0;
        gateway.CacheEntryBeforeOpenForTesting = _ =>
        {
            if (Interlocked.Increment(ref beforeOpen) == 1)
                throw new UnauthorizedAccessException("The cache file is delete-pending.");
        };
        gateway.CacheFileOpenedForTesting = _ => Interlocked.Increment(ref opened);
        var buffer = new byte[content.Length];

        gateway.Read(file.FileId, content.Length, buffer, 0).Should().Be(content.Length);

        buffer.Should().Equal(content);
        beforeOpen.Should().Be(2);
        opened.Should().Be(1);
    }

    [Fact]
    public void Read_returns_a_whole_file_larger_than_the_cache_limit()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider();
        var content = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var file = cloud.AddFile("", "oversized.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 32);
        var buffer = new byte[content.Length];
        gateway.CacheFileOpenedForTesting = _ =>
        {
            time.Advance(TimeSpan.FromMinutes(1));
            gateway.WaitForCacheCleanupForTestingAsync().GetAwaiter().GetResult();
        };

        gateway.Read(file.FileId, content.Length, buffer, 0).Should().Be(content.Length);

        buffer.Should().Equal(content);
        cloud.HttpHandler.Calls.Should().HaveCount(2);
        File.Exists(Path.Combine(temp.Path("cache"), file.FileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Read_keeps_an_oversized_whole_file_when_read_again_after_cleanup_interval()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var content = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var file = cloud.AddFile("", "active-oversized.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 32);
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var firstBuffer = new byte[16];
        gateway.Read(file.FileId, content.Length, firstBuffer, 0).Should().Be(firstBuffer.Length);
        firstBuffer.Should().Equal(content.Take(firstBuffer.Length));
        var cachedPath = Path.Combine(temp.Path("cache"), file.FileId);
        File.SetLastWriteTimeUtc(cachedPath, time.GetUtcNow().UtcDateTime.AddHours(-2));

        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.CacheCleanupStartedForTesting = () =>
        {
            cleanupStarted.TrySetResult();
            continueCleanup.Task.GetAwaiter().GetResult();
        };

        try
        {
            time.Advance(TimeSpan.FromMinutes(2));
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondBuffer = new byte[16];
            gateway.Read(file.FileId, content.Length, secondBuffer, 16).Should().Be(secondBuffer.Length);
            secondBuffer.Should().Equal(content.Skip(16).Take(secondBuffer.Length));
        }
        finally
        {
            continueCleanup.TrySetResult();
        }

        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(cachedPath).Should().BeTrue();
        cloud.HttpHandler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cache_cleanup_keeps_the_recently_read_old_file_and_evicts_the_unread_one()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var content = new byte[] { 1, 2, 3, 4, 5, 6 };
        var file = cloud.AddFile("", "recent.bin", content);
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.OK, content));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 10);
        var initialBuffer = new byte[content.Length];
        gateway.Read(file.FileId, content.Length, initialBuffer, 0).Should().Be(content.Length);

        var cache = temp.Path("cache");
        var recentlyRead = Path.Combine(cache, file.FileId);
        var unread = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(unread, new byte[content.Length]);
        File.SetLastWriteTimeUtc(recentlyRead, time.GetUtcNow().UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(unread, time.GetUtcNow().UtcDateTime.AddHours(-2));

        gateway.Read(file.FileId, content.Length, new byte[content.Length], 0).Should().Be(content.Length);
        gateway.StartCacheCleanupForTesting();
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(recentlyRead).Should().BeTrue();
        File.Exists(unread).Should().BeFalse();
    }

    [Fact]
    public async Task Cache_cleanup_keeps_a_recently_read_block_and_evicts_the_unread_file()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var content = new byte[] { 1, 2, 3, 4, 5, 6 };
        var file = cloud.AddFile("", "recent-block.bin", content);
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 10);
        var blockBuffer = new byte[content.Length];
        gateway.Read(file.FileId, content.Length, blockBuffer, 0).Should().Be(content.Length);

        var cache = temp.Path("cache");
        var blockPath = Path.Combine(cache, file.FileId + ".blocks", "0.blk");
        var unread = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(unread, new byte[content.Length]);
        File.SetLastWriteTimeUtc(blockPath, time.GetUtcNow().UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(unread, time.GetUtcNow().UtcDateTime.AddHours(-2));

        gateway.Read(file.FileId, content.Length, new byte[content.Length], 0).Should().Be(content.Length);
        gateway.StartCacheCleanupForTesting();
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(blockPath).Should().BeTrue();
        File.Exists(unread).Should().BeFalse();
    }

    [Fact]
    public async Task Cache_cleanup_removes_an_empty_blocks_directory_after_evicting_its_blocks()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 0);
        var blocksDirectory = Path.Combine(temp.Path("cache"), Guid.NewGuid().ToString("D") + ".blocks");
        Directory.CreateDirectory(blocksDirectory);
        var first = Path.Combine(blocksDirectory, "0.blk");
        var second = Path.Combine(blocksDirectory, "1.blk");
        File.WriteAllBytes(first, new byte[2]);
        File.WriteAllBytes(second, new byte[2]);
        File.SetLastWriteTimeUtc(first, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(second, time.GetUtcNow().UtcDateTime.AddHours(-1));

        gateway.StartCacheCleanupForTesting();
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(first).Should().BeFalse();
        File.Exists(second).Should().BeFalse();
        Directory.Exists(blocksDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Cache_cleanup_continues_after_a_block_disappears_during_accounting()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 0);
        var blocksDirectory = Path.Combine(temp.Path("cache"), Guid.NewGuid().ToString("D") + ".blocks");
        Directory.CreateDirectory(blocksDirectory);
        var disappeared = Path.Combine(blocksDirectory, "0.blk");
        var remaining = Path.Combine(blocksDirectory, "1.blk");
        File.WriteAllBytes(disappeared, new byte[2]);
        File.WriteAllBytes(remaining, new byte[2]);
        File.SetLastWriteTimeUtc(disappeared, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(remaining, time.GetUtcNow().UtcDateTime.AddHours(-1));
        var removed = 0;
        gateway.CacheEvictionCandidateBeforeInfoForTesting = path =>
        {
            if (path == disappeared && Interlocked.Exchange(ref removed, 1) == 0)
                File.Delete(path);
        };

        gateway.StartCacheCleanupForTesting();
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(disappeared).Should().BeFalse();
        File.Exists(remaining).Should().BeFalse();
    }

    [Fact]
    public async Task Startup_and_clear_cache_only_remove_strictly_owned_artifacts()
    {
        using var temp = new TestDirectory();
        var cache = temp.Path("cache");
        Directory.CreateDirectory(cache);
        var ownFileId = "abcdefab-cdef-4abc-8def-abcdefabcdef";
        var ownBlocksId = "12345678-1234-4abc-8def-123456789abc";
        var upperFileId = "ABCDEFAB-CDEF-4ABC-8DEF-111111111111";
        var upperBlocksId = "FEDCBA98-7654-4321-ABCD-FEDCBA987654";
        var nonce = Guid.NewGuid().ToString("N");
        var ownPart = Path.Combine(cache, $"{ownFileId}.{nonce}.part");
        var upperPart = Path.Combine(cache, $"{upperFileId}.{nonce}.part");
        var ownBlocks = Path.Combine(cache, ownBlocksId + ".blocks");
        var upperBlocks = Path.Combine(cache, upperBlocksId + ".blocks");
        var blockPart = Path.Combine(ownBlocks, $"0.blk.{nonce}.part");
        var leadingZeroBlockPart = Path.Combine(ownBlocks, $"00.blk.{nonce}.part");
        var blockFile = Path.Combine(ownBlocks, "0.blk");
        var leadingZeroBlockFile = Path.Combine(ownBlocks, "00.blk");
        var upperBlockPart = Path.Combine(upperBlocks, $"0.blk.{nonce}.part");
        var upperBlockFile = Path.Combine(upperBlocks, "0.blk");
        var ownFile = Path.Combine(cache, ownFileId);
        var upperFile = Path.Combine(cache, upperFileId);
        var unrelatedPart = Path.Combine(cache, "x.part");
        var unrelatedFile = Path.Combine(cache, "notes.bin");
        var writeDir = Path.Combine(cache, "write");
        var outboxDir = Path.Combine(cache, "outbox");
        Directory.CreateDirectory(ownBlocks);
        Directory.CreateDirectory(upperBlocks);
        Directory.CreateDirectory(writeDir);
        Directory.CreateDirectory(outboxDir);
        File.WriteAllText(ownPart, "partial");
        File.WriteAllText(upperPart, "partial");
        File.WriteAllText(blockPart, "partial");
        File.WriteAllText(leadingZeroBlockPart, "noncanonical partial");
        File.WriteAllText(blockFile, "cached");
        File.WriteAllText(leadingZeroBlockFile, "noncanonical block");
        File.WriteAllText(upperBlockPart, "foreign partial");
        File.WriteAllText(upperBlockFile, "foreign cache");
        File.WriteAllText(ownFile, "cached");
        File.WriteAllText(upperFile, "foreign");
        File.WriteAllText(unrelatedPart, "foreign");
        File.WriteAllText(unrelatedFile, "foreign");
        File.WriteAllText(Path.Combine(writeDir, ownFileId), "working copy");
        File.WriteAllText(Path.Combine(writeDir, $"{ownFileId}.{nonce}.part"), "working part");
        File.WriteAllText(Path.Combine(outboxDir, ownFileId), "outbox data");
        File.WriteAllText(Path.Combine(outboxDir, $"{ownFileId}.{nonce}.part"), "outbox part");

        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(cache);

        File.Exists(ownPart).Should().BeFalse();
        File.Exists(blockPart).Should().BeFalse();
        File.Exists(leadingZeroBlockPart).Should().BeTrue();
        File.Exists(leadingZeroBlockFile).Should().BeTrue();
        File.Exists(blockFile).Should().BeTrue();
        File.Exists(upperBlockPart).Should().BeTrue();
        File.Exists(upperBlockFile).Should().BeTrue();
        File.Exists(upperPart).Should().BeTrue();
        File.Exists(unrelatedPart).Should().BeTrue();

        gateway.ClearCache();

        var logPath = Path.Combine(Path.GetTempPath(), "BarkCloud.Drive.Engine.Tests",
            Environment.ProcessId.ToString(), "engine.log");
        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        log.Should().NotContain($"[ERROR] Очистка кеша {blockFile}");
        log.Should().NotContain($"[ERROR] Очистка кеша {blockPart}");

        File.Exists(ownFile).Should().BeFalse();
        Directory.Exists(ownBlocks).Should().BeFalse();
        Directory.Exists(upperBlocks).Should().BeTrue();
        File.Exists(upperBlockFile).Should().BeTrue();
        File.Exists(upperBlockPart).Should().BeTrue();
        File.Exists(upperFile).Should().BeTrue();
        File.Exists(upperPart).Should().BeTrue();
        File.Exists(unrelatedPart).Should().BeTrue();
        File.Exists(unrelatedFile).Should().BeTrue();
        File.Exists(Path.Combine(writeDir, ownFileId)).Should().BeTrue();
        File.Exists(Path.Combine(writeDir, $"{ownFileId}.{nonce}.part")).Should().BeTrue();
        File.Exists(Path.Combine(outboxDir, ownFileId)).Should().BeTrue();
        File.Exists(Path.Combine(outboxDir, $"{ownFileId}.{nonce}.part")).Should().BeTrue();
    }

    [Fact]
    public void Set_cache_directory_removes_only_its_owned_part_files()
    {
        using var temp = new TestDirectory();
        var cache = temp.Path("cache");
        var newCache = temp.Path("new-cache");
        Directory.CreateDirectory(newCache);
        var fileId = Guid.NewGuid().ToString("D");
        var nonce = Guid.NewGuid().ToString("N");
        var part = Path.Combine(newCache, $"{fileId}.{nonce}.part");
        var blocksDirectory = Path.Combine(newCache, fileId + ".blocks");
        Directory.CreateDirectory(blocksDirectory);
        var blockPart = Path.Combine(blocksDirectory, $"0.blk.{nonce}.part");
        var blockFile = Path.Combine(blocksDirectory, "0.blk");
        File.WriteAllText(part, "partial");
        File.WriteAllText(blockPart, "partial");
        File.WriteAllText(blockFile, "cached");

        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(cache);

        gateway.SetCacheDir(newCache);

        File.Exists(part).Should().BeFalse();
        File.Exists(blockPart).Should().BeFalse();
        File.Exists(blockFile).Should().BeTrue();
    }

    [Fact]
    public async Task Cache_cleanup_does_not_traverse_a_blocks_directory_junction()
    {
        using var temp = new TestDirectory();
        var cache = temp.Path("cache");
        var target = temp.Path("target");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(target);
        var externalBlock = Path.Combine(target, "0.blk");
        File.WriteAllBytes(externalBlock, new byte[8]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        File.SetLastWriteTimeUtc(externalBlock, time.GetUtcNow().UtcDateTime.AddHours(-2));
        var link = Path.Combine(cache, Guid.NewGuid().ToString("D") + ".blocks");
        CreateDirectoryJunction(link, target);

        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(cache, time, cacheLimitBytesForTesting: 0);
        gateway.StartCacheCleanupForTesting();
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(externalBlock).Should().BeTrue();
        Directory.Exists(link).Should().BeTrue();
        gateway.ClearCache();
        File.Exists(externalBlock).Should().BeTrue();
        Directory.Exists(link).Should().BeTrue();
    }

    [Fact]
    public async Task Clear_cache_during_cleanup_completes_without_an_error()
    {
        using var temp = new TestDirectory();
        var cache = temp.Path("cache");
        var file = Path.Combine(cache, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(cache);
        File.WriteAllText(file, "cached");
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(cache);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.CacheCleanupStartedForTesting = () =>
        {
            cleanupStarted.TrySetResult();
            continueCleanup.Task.GetAwaiter().GetResult();
        };

        gateway.StartCacheCleanupForTesting();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            gateway.ClearCache();
            File.Exists(file).Should().BeFalse();
        }
        finally
        {
            continueCleanup.TrySetResult();
        }

        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_waits_for_cache_cleanup_to_finish()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var gateway = cloud.CreateGateway(temp.Path("cache"), new FakeTimeProvider());
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var disposeWaiting = new ManualResetEventSlim();
        gateway.CacheCleanupStartedForTesting = () =>
        {
            cleanupStarted.TrySetResult();
            continueCleanup.Task.GetAwaiter().GetResult();
        };
        gateway.CacheCleanupWaitForTesting = disposeWaiting.Set;
        gateway.StartCacheCleanupForTesting();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = Task.Run(gateway.Dispose);
        try
        {
            disposeWaiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            dispose.IsCompleted.Should().BeFalse();
        }
        finally
        {
            continueCleanup.TrySetResult();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cache_cleanup_does_not_run_more_than_once_per_minute()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time, cacheLimitBytesForTesting: 10);
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var first = Path.Combine(temp.Path("cache"), Guid.NewGuid().ToString("D"));
        var second = Path.Combine(temp.Path("cache"), Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(first, new byte[6]);
        File.WriteAllBytes(second, new byte[6]);
        File.SetLastWriteTimeUtc(first, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(second, time.GetUtcNow().UtcDateTime.AddHours(-1));

        time.Advance(TimeSpan.FromSeconds(59));
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(1));
        await gateway.WaitForCacheCleanupForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(first).Should().BeFalse();
        File.Exists(second).Should().BeTrue();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body)
        => new(status) { Content = new ByteArrayContent(body) };

    private static void CreateDirectoryJunction(string path, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить cmd.exe для создания junction");
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.ExitCode.Should().Be(0, $"{output}{error}");
    }
}
