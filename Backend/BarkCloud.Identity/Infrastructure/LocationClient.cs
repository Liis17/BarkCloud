using BarkCloud.GrpcServer.Metrics;

using System.Text.Json;

namespace BarkCloud.Identity.Infrastructure;

public class LocationClient
{
    private const string BaseUrl = "http://ip-api.com/json/";

    // Геолокация необязательна (только текст письма и название места устройства): ждём недолго, дальше — «-».
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<LocationClient> _logger;

    public LocationClient(HttpClient httpClient, MetricsCollector metrics, ILogger<LocationClient> logger)
    {
        _httpClient = httpClient;
        _metrics = metrics;
        _logger = logger;
    }

    public virtual async Task<IpLocation?> GetLocation(string ip)
    {
        _logger.LogDebug("Запрос геолокации для IP: {IpAddress}", ip);
        _metrics.Increment("geolocation_requests");

        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);

            var url = $"{BaseUrl}{ip}?lang=ru";
            var response = await _httpClient.GetAsync(url, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("geolocation_errors");
                _logger.LogWarning(
                    "Не удалось получить геолокацию для IP {IpAddress}. Status: {StatusCode}",
                    ip,
                    response.StatusCode
                );
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(timeout.Token);
            var location = JsonSerializer.Deserialize<IpLocation>(content);

            if (location != null)
            {
                _metrics.Increment("geolocation_success");
                _logger.LogInformation(
                    "Получена геолокация для IP {IpAddress}: {Country}, {Region}, {City}",
                    ip,
                    location.Country,
                    location.RegionName,
                    location.City
                );
            }

            return location;
        }
        catch (OperationCanceledException)
        {
            // Внешнего токена нет: отмена — это только наш таймаут.
            _metrics.Increment("geolocation_errors");
            _metrics.Increment("geolocation_timeouts");
            _logger.LogWarning(
                "Геолокация для IP {IpAddress} не получена за {Timeout} с",
                ip,
                RequestTimeout.TotalSeconds
            );
            return null;
        }
        catch (HttpRequestException ex)
        {
            _metrics.Increment("geolocation_errors");
            _logger.LogError(
                ex,
                "HTTP ошибка при запросе геолокации для IP {IpAddress}",
                ip
            );
            return null;
        }
        catch (Exception ex)
        {
            _metrics.Increment("geolocation_errors");
            _logger.LogError(
                ex,
                "Неожиданная ошибка при получении геолокации для IP {IpAddress}",
                ip
            );
            return null;
        }
    }
}