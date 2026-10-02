using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarkCloud.GrpcServer.XAuth;

public class RevocationSyncService(
    TokenRevocationCache cache,
    IRevocationFeed feed,
    ILogger<RevocationSyncService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    // Равен overlap incremental-запроса: отзыв, закоммиченный позже overlap, incremental не вернёт,
    // поэтому полный снимок повторяется, чтобы такая запись дошла до кэша.
    private static readonly TimeSpan FullResyncInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DateTime _serverTime;
    private DateTimeOffset _lastFullSync;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // ExecuteAsync в .NET 10 запускается в фоне. Снимок загружаем здесь,
        // чтобы следующий hosted service (Kestrel) не принимал запросы с пустым кэшем.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Apply(await feed.FetchAsync(null, cancellationToken));
                _lastFullSync = _timeProvider.GetUtcNow();
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < 6)
            {
                logger.LogWarning(ex, "Не удалось загрузить снимок отзывов сессий, попытка {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), _timeProvider, cancellationToken);
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
            try
            {
                var fullResync = _timeProvider.GetUtcNow() - _lastFullSync >= FullResyncInterval;
                Apply(await feed.FetchAsync(fullResync ? null : _serverTime.AddMinutes(-1), stoppingToken));
                if (fullResync)
                {
                    _lastFullSync = _timeProvider.GetUtcNow();
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Не удалось обновить отзывы сессий; сохранён предыдущий кэш");
            }
        }
    }

    private void Apply(RevocationBatch batch)
    {
        foreach (var session in batch.Sessions)
        {
            cache.Revoke(session.UserId, session.DeviceId, session.RevokedAt, session.ExpiresAt);
        }

        cache.Cleanup();
        _serverTime = batch.ServerTime;
    }
}
