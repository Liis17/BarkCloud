using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;

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
    // Один проход ограничен, чтобы фоновая служба могла сообщить об ошибке и
    // начать новый проход после паузы. Сам процесс больше не падает из-за MinIO.
    private const int MaxAttemptsPerPass = 10;

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
        var failures = new List<Exception>();

        foreach (var (profile, client) in _registry.GetAllProfiles())
        {
            try
            {
                await EnsureBucketExistsAsync(client, profile, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при инициализации S3-профиля {ProfileId}, бакет {BucketName}", profile.ProfileId, profile.BucketName);
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Не удалось инициализировать несколько S3-профилей.", failures);

        _logger.LogInformation("Инициализация S3 бакетов успешно завершена");
    }

    /// <summary>
    /// Проверяет существование бакета и создает его при необходимости
    /// </summary>
    private async Task EnsureBucketExistsAsync(
        IAmazonS3 client,
        StorageProfileOptions profile,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureBucketExistsOnceAsync(client, profile, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttemptsPerPass && IsTransientStartupException(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt, 5));
                _logger.LogWarning(
                    "S3 недоступен для бакета {BucketName}; повтор {Attempt}/{MaxAttempts} через {DelaySeconds} с",
                    profile.BucketName,
                    attempt + 1,
                    MaxAttemptsPerPass,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task EnsureBucketExistsOnceAsync(
        IAmazonS3 client,
        StorageProfileOptions profile,
        CancellationToken cancellationToken)
    {
        var bucketName = profile.BucketName;
        try
        {
            if (profile.IsR2)
            {
                // Object Read/Write credentials do not need bucket-administration permissions.
                await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    MaxKeys = 1
                }, cancellationToken);
                _logger.LogInformation("R2-бакет {BucketName} доступен", bucketName);
                return;
            }

            // Для локального S3 проверяем существование и при необходимости создаём бакет.
            try
            {
                await client.GetBucketLocationAsync(bucketName, cancellationToken);
                _logger.LogInformation("Бакет {BucketName} уже существует", bucketName);
                return;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!profile.IsActive)
                {
                    throw new InvalidOperationException(
                        $"Inactive S3 profile '{profile.ProfileId}' points to missing bucket '{bucketName}'.", ex);
                }
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
