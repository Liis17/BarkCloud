using Amazon.S3;
using Amazon.S3.Model;

namespace BarkCloud.Files.Infrastructure;

/// <summary>
/// Сервис для автоматической инициализации S3 бакетов при запуске приложения.
/// Поддерживает бакеты на разных S3-совместимых хранилищах.
/// </summary>
public class S3BucketInitializer
{
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
    public async Task InitializeBucketsAsync()
    {
        _logger.LogInformation("Начинается инициализация S3 бакетов...");

        foreach (var (bucketName, client) in _registry.GetAllBuckets())
        {
            try
            {
                await EnsureBucketExistsAsync(client, bucketName);
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
    private async Task EnsureBucketExistsAsync(IAmazonS3 client, string bucketName)
    {
        try
        {
            // Проверяем существование бакета через попытку получить его локацию
            try
            {
                await client.GetBucketLocationAsync(bucketName);
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

            await client.PutBucketAsync(putBucketRequest);
            _logger.LogInformation("Бакет {BucketName} создан как приватный (доступ только через presigned URL)", bucketName);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Бакет уже существует (может быть создан параллельно)
            _logger.LogWarning("Бакет {BucketName} уже существует (конфликт при создании)", bucketName);
        }
    }
}
