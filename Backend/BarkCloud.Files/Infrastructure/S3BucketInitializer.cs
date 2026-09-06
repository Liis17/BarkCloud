using Amazon.S3;
using Amazon.S3.Model;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace BarkCloud.Files.Infrastructure;

/// <summary>
/// Сервис для автоматической инициализации S3 бакетов при запуске приложения.
/// Поддерживает бакеты на разных S3-совместимых хранилищах.
/// </summary>
public class S3BucketInitializer
{
    private const int MaxAttempts = 10;

    private readonly S3BucketRegistry _registry;
    private readonly ILogger<S3BucketInitializer> _logger;

    public S3BucketInitializer(S3BucketRegistry registry, ILogger<S3BucketInitializer> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Инициализирует все необходимые S3 бакеты
    /// </summary>
    public async Task InitializeBucketsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Начинается инициализация S3 бакетов...");

        foreach (var (bucketName, client) in _registry.GetAllBuckets())
        {
            try
            {
                await EnsureBucketExistsAsync(client, bucketName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при инициализации бакета {BucketName}", bucketName);
                throw;
            }
        }

        _logger.LogInformation("Инициализация S3 бакетов успешно завершена");
    }

    /// <summary>
    /// Проверяет существование бакета и создает его при необходимости
    /// </summary>
    private async Task EnsureBucketExistsAsync(
        IAmazonS3 client,
        string bucketName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureBucketExistsOnceAsync(client, bucketName, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransientStartupException(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt, 5));
                _logger.LogWarning(
                    "S3 недоступен для бакета {BucketName}; повтор {Attempt}/{MaxAttempts} через {DelaySeconds} с",
                    bucketName,
                    attempt + 1,
                    MaxAttempts,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task EnsureBucketExistsOnceAsync(
        IAmazonS3 client,
        string bucketName,
        CancellationToken cancellationToken)
    {
        try
        {
            // Проверяем существование бакета через попытку получить его локацию
            try
            {
                await client.GetBucketLocationAsync(bucketName, cancellationToken);
                _logger.LogInformation("Бакет {BucketName} уже существует", bucketName);
                return;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Бакет не найден, продолжаем создание
            }

            // Создаем бакет
            _logger.LogInformation("Создание бакета {BucketName}...", bucketName);
            var putBucketRequest = new PutBucketRequest
            {
                BucketName = bucketName,
                UseClientRegion = false
            };

            await client.PutBucketAsync(putBucketRequest, cancellationToken);
            _logger.LogInformation("Бакет {BucketName} создан как приватный (доступ только через presigned URL)", bucketName);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Бакет уже существует (может быть создан параллельно)
            _logger.LogWarning("Бакет {BucketName} уже существует (конфликт при создании)", bucketName);
        }
    }

    internal static bool IsTransientStartupException(Exception exception)
    {
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsTransientStartupException);

        if (exception is HttpRequestException or SocketException or TimeoutException)
            return true;

        if (exception is AmazonS3Exception s3Exception)
        {
            var statusCode = (int)s3Exception.StatusCode;
            if (statusCode is (int)HttpStatusCode.RequestTimeout
                or (int)HttpStatusCode.TooManyRequests
                || statusCode >= 500)
                return true;
        }

        return exception.InnerException is not null && IsTransientStartupException(exception.InnerException);
    }
}
