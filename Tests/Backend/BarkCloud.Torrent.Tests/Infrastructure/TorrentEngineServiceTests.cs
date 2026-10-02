using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Tests._Helpers;

namespace BarkCloud.Torrent.Tests.Infrastructure;

public class TorrentEngineServiceTests : IAsyncLifetime
{
    private readonly EngineHarness _harness = new();

    private TestEngine Engine => _harness.Engine;

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    /// <summary>Менеджер всё ещё зарегистрирован в движке: тот же торрент повторно не добавить.</summary>
    private async Task AssertStillInEngineAsync()
        => await ((Func<Task>)(() => _harness.AddAsync())).Should().ThrowAsync<DuplicateTorrentException>();

    /// <summary>Менеджера в движке нет: торрент добавляется заново.</summary>
    private async Task AssertGoneFromEngineAsync()
        => await ((Func<Task>)(() => _harness.AddAsync())).Should().NotThrowAsync();

    [Fact]
    public async Task RemoveAsync_WhenStopFails_KeepsTorrentManageableUntilRetrySucceeds()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        Engine.StopHook = _ => throw new IOException("stop failed");

        await ((Func<Task>)(() => Engine.RemoveAsync(id, deleteData: false))).Should().ThrowAsync<IOException>();

        Engine.Get(id).Should().NotBeNull();
        await AssertStillInEngineAsync();

        Engine.StopHook = null;
        await Engine.RemoveAsync(id, deleteData: false);

        Engine.Get(id).Should().BeNull();
        await AssertGoneFromEngineAsync();
    }

    [Fact]
    public async Task RemoveAsync_WhenEngineRemoveFails_KeepsTorrentManageableUntilRetrySucceeds()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        Engine.RemoveHook = (_, _) => throw new IOException("remove failed");

        await ((Func<Task>)(() => Engine.RemoveAsync(id, deleteData: false))).Should().ThrowAsync<IOException>();

        Engine.Get(id).Should().NotBeNull();
        await AssertStillInEngineAsync();

        Engine.RemoveHook = null;
        await Engine.RemoveAsync(id, deleteData: false);

        Engine.Get(id).Should().BeNull();
        await AssertGoneFromEngineAsync();
    }

    [Fact]
    public async Task RemoveAsync_WhenFailureComesAfterEngineUnregistered_RetryFinishesDataDeletion()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        // MonoTorrent снимает менеджер с движка, а потом падает на удалении файлов.
        Engine.RemoveHook = async (manager, mode) =>
        {
            await Engine.RemoveViaEngineAsync(manager, MonoTorrent.Client.RemoveMode.CacheDataOnly);
            throw new IOException("delete data failed");
        };

        await ((Func<Task>)(() => Engine.RemoveAsync(id, deleteData: true))).Should().ThrowAsync<IOException>();

        Engine.Get(id).Should().NotBeNull();
        File.Exists(_harness.DataFile).Should().BeTrue();

        Engine.RemoveHook = null;
        await Engine.RemoveAsync(id, deleteData: true);

        Engine.Get(id).Should().BeNull();
        File.Exists(_harness.DataFile).Should().BeFalse();
        await AssertGoneFromEngineAsync();
    }

    [Fact]
    public async Task RemoveAsync_Success_RemovesFromRegistryAndEngine()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);

        await Engine.RemoveAsync(id, deleteData: true);

        Engine.Get(id).Should().BeNull();
        Engine.All.Should().BeEmpty();
        File.Exists(_harness.DataFile).Should().BeFalse();
        await AssertGoneFromEngineAsync();
    }

    [Fact]
    public async Task RemoveAsync_UnknownId_IsNoOp()
    {
        await Engine.RemoveAsync(Guid.NewGuid(), deleteData: true);

        Engine.StopCalls.Should().Be(0);
        Engine.RemoveCalls.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAsync_ConcurrentCallsForSameTorrent_AreSerialized()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.StopHook = _ => release.Task;

        var first = Engine.RemoveAsync(id, deleteData: false);
        var second = Engine.RemoveAsync(id, deleteData: false);
        await Task.Delay(100);

        // Второй вызов ждёт замок торрента и не доходит до остановки.
        Engine.StopCalls.Should().Be(1);
        second.IsCompleted.Should().BeFalse();

        release.SetResult();
        await Task.WhenAll(first, second);

        Engine.RemoveCalls.Should().Be(1);
        Engine.Get(id).Should().BeNull();
    }

    [Fact]
    public async Task PauseAsync_WhenEngineFails_ThrowsAndKeepsTorrentRegistered()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        Engine.PauseHook = _ => throw new IOException("pause failed");

        await ((Func<Task>)(() => Engine.PauseAsync(id))).Should().ThrowAsync<IOException>();

        Engine.Get(id).Should().NotBeNull();
    }

    [Fact]
    public async Task ResumeAsync_WhenEngineFails_ThrowsAndKeepsTorrentRegistered()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        Engine.StartHook = _ => throw new IOException("start failed");

        await ((Func<Task>)(() => Engine.ResumeAsync(id))).Should().ThrowAsync<IOException>();

        Engine.Get(id).Should().NotBeNull();
    }

    [Fact]
    public async Task PauseAsync_AfterFailedPause_CanBeRetried()
    {
        var id = Guid.NewGuid();
        await _harness.AddAsync(id);
        var calls = 0;
        Engine.PauseHook = _ => ++calls == 1 ? throw new IOException("pause failed") : Task.CompletedTask;

        await ((Func<Task>)(() => Engine.PauseAsync(id))).Should().ThrowAsync<IOException>();
        await Engine.PauseAsync(id);

        calls.Should().Be(2);
    }
}
