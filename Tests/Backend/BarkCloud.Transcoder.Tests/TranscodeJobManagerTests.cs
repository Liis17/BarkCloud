using BarkCloud.Transcoder;

namespace BarkCloud.Transcoder.Tests;

public sealed class TranscodeJobManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartRejectsSecondJobUntilCurrentFinishes()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "source"), "source");
        var runner = new BlockingRunner();
        var manager = new TranscodeJobManager(new TranscodePaths(_root), runner);

        var first = manager.Start(new StartTranscodeRequest("source", "first.webm", 360, 1));
        var second = manager.Start(new StartTranscodeRequest("source", "second.webm", 720, 1));

        Assert.False(first.Conflict);
        Assert.True(second.Conflict);
        Assert.NotNull(manager.Get(first.Job!.Id));

        runner.Finish();
        await runner.Started;
        await WaitForTerminalStatus(manager, first.Job.Id);
    }

    [Fact]
    public async Task CancelWaitsForRunnerToStop()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "source"), "source");
        var runner = new BlockingRunner();
        var manager = new TranscodeJobManager(new TranscodePaths(_root), runner);
        var start = manager.Start(new StartTranscodeRequest("source", "output.webm", 360, 1));

        await runner.Started;
        var cancelled = await manager.CancelAsync(start.Job!.Id);

        Assert.Equal("cancelled", cancelled!.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task WaitForTerminalStatus(TranscodeJobManager manager, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.Get(id)?.Status == "running")
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal("completed", manager.Get(id)!.Status);
    }

    private sealed class BlockingRunner : ITranscoderProcessRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task RunAsync(
            string inputPath,
            string outputPath,
            int height,
            Action<int> reportProgress,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            reportProgress(42);
            await _finish.Task.WaitAsync(cancellationToken);
        }

        public void Finish() => _finish.TrySetResult();
    }
}
