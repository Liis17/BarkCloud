namespace BarkCloud.Transcoder;

public sealed class TranscodeJobManager(TranscodePaths paths, ITranscoderProcessRunner runner)
{
    private readonly object _sync = new();
    private Job? _current;

    public TranscodeStartResult Start(StartTranscodeRequest request)
    {
        if (request.Generation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Generation), "Поколение задания должно быть положительным.");
        }

        if (!FfmpegCommand.SupportedHeights.Contains(request.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Height), "Высота не входит в поддерживаемую лестницу.");
        }

        lock (_sync)
        {
            if (_current?.Status == "running")
            {
                return new TranscodeStartResult(true, null);
            }

            _current?.Cancellation.Dispose();
            var inputPath = paths.ResolveInput(request.Input);
            var outputPath = paths.ResolveOutput(request.Output);
            var job = new Job(Guid.NewGuid().ToString("N"), inputPath, outputPath, request.Height, request.Generation);
            _current = job;
            job.Execution = Task.Run(() => ExecuteAsync(job));
            return new TranscodeStartResult(false, Snapshot(job));
        }
    }

    public TranscodeJobResponse? Get(string id)
    {
        lock (_sync)
        {
            return _current?.Id == id ? Snapshot(_current) : null;
        }
    }

    public TranscodeJobResponse? GetCurrent()
    {
        lock (_sync)
        {
            return _current is null ? null : Snapshot(_current);
        }
    }

    public async Task<TranscodeJobResponse?> CancelAsync(string id)
    {
        Job? job;
        Task? execution;
        lock (_sync)
        {
            if (_current?.Id != id)
            {
                return null;
            }

            job = _current;
            if (job.Status != "running")
            {
                return Snapshot(job);
            }

            job.Cancellation.Cancel();
            execution = job.Execution;
        }

        if (execution is not null)
        {
            await execution;
        }

        return Get(id);
    }

    private async Task ExecuteAsync(Job job)
    {
        try
        {
            await runner.RunAsync(
                job.InputPath,
                job.OutputPath,
                job.Height,
                progress => UpdateProgress(job, progress),
                job.Cancellation.Token);
            lock (_sync)
            {
                job.Status = "completed";
                job.Progress = 100;
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                job.Status = "cancelled";
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                job.Status = "failed";
                job.Error = exception.Message.Length > 1000 ? exception.Message[^1000..] : exception.Message;
            }
        }
    }

    private void UpdateProgress(Job job, int progress)
    {
        lock (_sync)
        {
            if (job.Status == "running")
            {
                job.Progress = Math.Clamp(progress, 0, 99);
            }
        }
    }

    private static TranscodeJobResponse Snapshot(Job job) =>
        new(job.Id, job.Generation, job.Status, job.Progress, job.Error);

    private sealed class Job(string id, string inputPath, string outputPath, int height, long generation)
    {
        public string Id { get; } = id;
        public string InputPath { get; } = inputPath;
        public string OutputPath { get; } = outputPath;
        public int Height { get; } = height;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public string Status { get; set; } = "running";
        public int Progress { get; set; }
        public string? Error { get; set; }
        public Task? Execution { get; set; }
    }
}

public sealed record StartTranscodeRequest(string Input, string Output, int Height, long Generation);

public sealed record TranscodeJobResponse(string Id, long Generation, string Status, int Progress, string? Error);

public sealed record TranscodeStartResult(bool Conflict, TranscodeJobResponse? Job);
