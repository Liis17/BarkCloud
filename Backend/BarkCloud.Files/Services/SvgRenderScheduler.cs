namespace BarkCloud.Files.Services;

/// <summary>Один слот рендерера на экземпляр Files; ожидающие загрузки обходят backfill.</summary>
internal sealed class SvgRenderScheduler
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _uploads = new();
    private readonly LinkedList<Waiter> _backfills = new();
    private bool _held;

    public Task<IDisposable?> AcquireUploadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        AcquireAsync(_uploads, timeout, cancellationToken);

    public async Task<IDisposable> AcquireBackfillAsync(CancellationToken cancellationToken)
    {
        return (await AcquireAsync(_backfills, null, cancellationToken))!;
    }

    private async Task<IDisposable?> AcquireAsync(
        LinkedList<Waiter> queue,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_sync)
        {
            if (!_held && _uploads.Count == 0 && _backfills.Count == 0)
            {
                _held = true;
                return new Lease(this);
            }

            waiter = new Waiter();
            waiter.Node = queue.AddLast(waiter);
        }

        try
        {
            return timeout is { } limit
                ? await waiter.Completion.Task.WaitAsync(limit, cancellationToken)
                : await waiter.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            if (Remove(waiter))
                return null;

            // The slot was granted concurrently with the timeout; consume the lease.
            return await waiter.Completion.Task;
        }
        catch (OperationCanceledException)
        {
            if (!Remove(waiter))
                (await waiter.Completion.Task).Dispose();
            throw;
        }
    }

    private bool Remove(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is null)
                return false;

            (waiter.Node.List == _uploads ? _uploads : _backfills).Remove(waiter.Node);
            waiter.Node = null;
            return true;
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            var waiter = _uploads.First?.Value ?? _backfills.First?.Value;
            if (waiter is null)
            {
                _held = false;
                return;
            }

            var queue = waiter.Node!.List!;
            queue.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetResult(new Lease(this));
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private SvgRenderScheduler? _owner;

        public Lease(SvgRenderScheduler owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
