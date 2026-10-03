namespace BarkCloud.Files.Services;

/// <summary>Один пересчёт на источник, последний успешный замер и отмена при остановке хоста.</summary>
internal sealed class StorageStatsCache<T>(
    Func<CancellationToken, Task<T>> read,
    TimeProvider time,
    ILogger logger,
    CancellationToken stoppingToken) where T : class
{
    private readonly object _gate = new();
    private T? _value;
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset? _attemptedAt;
    private string _state = "loading";
    private Task? _refresh;

    public bool HasValue { get { lock (_gate) return _value is not null; } }

    public async Task<(T? Value, string State, DateTimeOffset? UpdatedAt)> GetAsync(
        bool nonBlocking, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? refresh;
        lock (_gate)
        {
            if ((_refresh is null || _refresh.IsCompleted)
                && (_attemptedAt is null || time.GetUtcNow() - _attemptedAt >= TimeSpan.FromMinutes(5))
                && !stoppingToken.IsCancellationRequested)
            {
                _state = _value is null ? "loading" : "refreshing";
                _refresh = Task.Run(RefreshAsync, CancellationToken.None);
            }
            refresh = _refresh;
        }

        if (!nonBlocking && refresh is not null)
            await refresh.WaitAsync(cancellationToken);

        lock (_gate) return (_value, _state, _updatedAt);
    }

    private async Task RefreshAsync()
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            var value = await read(stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _value = value;
                _updatedAt = _attemptedAt = time.GetUtcNow();
                _state = "ready";
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Хост завершается; замер не публикуем.
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _attemptedAt = time.GetUtcNow();
                _state = "error";
            }
            logger.LogWarning(exception, "Обновление статистики {Source} не выполнено; сохраняем последний замер", typeof(T).Name);
        }
    }
}
