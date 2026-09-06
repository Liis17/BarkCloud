using Microsoft.Extensions.Hosting;

namespace BarkCloud.Files.Infrastructure;

/// <summary>
/// Инициализирует S3-бакеты в фоне. Kestrel должен запуститься даже тогда, когда
/// MinIO ещё поднимается: иначе Docker получает бесконечный crash-loop, а nginx — 502.
/// </summary>
public sealed class S3BucketInitializationHostedService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly S3BucketInitializer _initializer;
    private readonly ILogger<S3BucketInitializationHostedService> _logger;

    public S3BucketInitializationHostedService(
        S3BucketInitializer initializer,
        ILogger<S3BucketInitializationHostedService> logger)
    {
        _initializer = initializer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _initializer.InitializeBucketsAsync(stoppingToken);
                _logger.LogInformation("Фоновая инициализация S3 бакетов завершена");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (S3BucketInitializer.IsTransientStartupException(ex))
            {
                _logger.LogError(
                    ex,
                    "MinIO пока недоступен. Files продолжит работу и повторит инициализацию через {DelaySeconds} с",
                    RetryDelay.TotalSeconds);
            }
            catch (Exception ex)
            {
                // Ошибка конфигурации (например, неверные credentials) не должна
                // перезапускать весь контейнер. Операции S3 сами вернут ошибку,
                // а причина останется в stdout/Seq для исправления конфигурации.
                _logger.LogError(ex, "Не удалось инициализировать S3-бакеты; повтор не выполняется");
                return;
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
