namespace BarkCloud.Files.Services;

/// <summary>
/// Ограничивает число одновременных legacy-загрузок и суммарный объём, который под них
/// буферизуется на временном диске. Слот ждёт очередь до <see cref="LegacyUploadOptions.QueueTimeout"/>,
/// байтовый бюджет проверяется сразу.
/// </summary>
public sealed class LegacyUploadBudget
{
    private readonly LegacyUploadOptions _options;
    private readonly SemaphoreSlim _slots;
    private long _bufferedBytes;

    public LegacyUploadBudget(LegacyUploadOptions options)
    {
        _options = options;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    public long BufferedBytes => Interlocked.Read(ref _bufferedBytes);

    /// <summary>Возвращает lease или null, если слот не освободился вовремя либо бюджет байт исчерпан.</summary>
    public async Task<Lease?> TryAcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        // Тело, чуть превышающее бюджет из-за multipart-обвязки, не должно отклоняться навсегда.
        bytes = Math.Min(Math.Max(0, bytes), _options.MaxBufferedBytes);

        if (!await _slots.WaitAsync(_options.QueueTimeout, cancellationToken))
            return null;

        if (Interlocked.Add(ref _bufferedBytes, bytes) > _options.MaxBufferedBytes)
        {
            Interlocked.Add(ref _bufferedBytes, -bytes);
            _slots.Release();
            return null;
        }

        return new Lease(this, bytes);
    }

    public sealed class Lease : IDisposable
    {
        private readonly LegacyUploadBudget _owner;
        private readonly long _bytes;
        private int _disposed;

        internal Lease(LegacyUploadBudget owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Interlocked.Add(ref _owner._bufferedBytes, -_bytes);
            _owner._slots.Release();
        }
    }
}
