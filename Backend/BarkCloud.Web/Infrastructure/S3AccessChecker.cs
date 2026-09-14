using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using System.Net;

namespace BarkCloud.Web.Infrastructure;

public sealed record S3AccessCheckRequest(
    string ServiceUrl,
    string AccessKey,
    string SecretKey,
    string BucketName,
    bool IsR2);

public sealed record StorageAccessCheckResult(bool Success, string Message);

/// <summary>Проверяет доступ к S3-профилю без создания или изменения объектов.</summary>
public class S3AccessChecker
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    public virtual async Task<StorageAccessCheckResult> CheckAsync(
        S3AccessCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        var serviceUrl = request.ServiceUrl.Trim().TrimEnd('/');
        var bucketName = request.BucketName.Trim();

        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new StorageAccessCheckResult(false, "Укажите endpoint S3");
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            return new StorageAccessCheckResult(false, "Endpoint S3 должен быть абсолютным HTTP(S)-адресом");
        if (string.IsNullOrWhiteSpace(request.AccessKey))
            return new StorageAccessCheckResult(false, "Укажите access key");
        if (string.IsNullOrWhiteSpace(request.SecretKey))
            return new StorageAccessCheckResult(false, "Укажите secret key");
        if (string.IsNullOrWhiteSpace(bucketName))
            return new StorageAccessCheckResult(false, "Укажите имя бакета");

        try
        {
            var config = new AmazonS3Config
            {
                ServiceURL = request.IsR2 ? R2Endpoint(serviceUrl) : serviceUrl,
                ForcePathStyle = true
            };
            if (request.IsR2)
            {
                config.AuthenticationRegion = "auto";
                config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
                config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
            }

            using var client = new AmazonS3Client(
                new BasicAWSCredentials(request.AccessKey.Trim(), request.SecretKey), config);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);

            if (request.IsR2)
            {
                // R2 Object Read/Write credentials may not have bucket-administration permissions.
                await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    MaxKeys = 1
                }, timeout.Token);
            }
            else
            {
                await client.GetBucketLocationAsync(bucketName, timeout.Token);
            }

            return new StorageAccessCheckResult(true, $"Доступ к бакету «{bucketName}» подтверждён");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StorageAccessCheckResult(false, "Проверка доступа превысила 10 секунд");
        }
        catch (AmazonS3Exception exception)
        {
            return new StorageAccessCheckResult(false, MapS3Error(exception));
        }
        catch (HttpRequestException)
        {
            return new StorageAccessCheckResult(false, "S3 endpoint недоступен или не отвечает");
        }
        catch (TimeoutException)
        {
            return new StorageAccessCheckResult(false, "Проверка доступа превысила 10 секунд");
        }
        catch (AmazonClientException)
        {
            return new StorageAccessCheckResult(false, "S3 endpoint недоступен или не отвечает");
        }
    }

    private static string MapS3Error(AmazonS3Exception exception) =>
        exception.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                "S3 отклонил доступ к бакету. Проверьте ключи и права",
            HttpStatusCode.NotFound => "Указанный бакет не найден на этом S3 endpoint",
            _ => "S3 вернул ошибку при проверке доступа"
        };

    private static string R2Endpoint(string serviceUrl)
    {
        var uri = new UriBuilder(serviceUrl) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return uri.Uri.ToString().TrimEnd('/');
    }
}
