using System.Text.Json;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Последний результат detached-операции над cloud-web.</summary>
public sealed record MaintenanceOperationStatus(
    string OperationId,
    string Kind,
    string State,
    string? Message,
    string? Diagnostic,
    DateTime UpdatedAtUtc);

/// <summary>
/// Читает маркер self-update/restart из persistent maintenance volume. Helper пишет файл
/// независимо от жизненного цикла web, поэтому ошибка нового контейнера не теряется при
/// перезапуске процесса.
/// </summary>
public sealed class MaintenanceOperationStore
{
    public const string DefaultStateFilePath = "/app/maintenance/last-operation.json";
    public const string DefaultLogFilePath = "/app/maintenance/self-update.log";

    private readonly ILogger<MaintenanceOperationStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public MaintenanceOperationStore(IConfiguration configuration, ILogger<MaintenanceOperationStore> logger)
    {
        StateFilePath = configuration["Docker:MaintenanceStateFile"] ?? DefaultStateFilePath;
        LogFilePath = configuration["Docker:MaintenanceLogFile"] ?? DefaultLogFilePath;
        _logger = logger;
    }

    public string StateFilePath { get; }
    public string LogFilePath { get; }

    public async Task<MaintenanceOperationStatus?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return null;

            await using var stream = File.OpenRead(StateFilePath);
            var status = await JsonSerializer.DeserializeAsync<MaintenanceOperationStatus>(
                stream, _jsonOptions, cancellationToken);
            if (status is null)
                return null;

            if (string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase)
                && File.Exists(LogFilePath))
            {
                var log = await File.ReadAllTextAsync(LogFilePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(log))
                {
                    var diagnostic = $"{status.Diagnostic}\nЛог helper:\n{log.Trim()}";
                    status = status with { Diagnostic = Limit(diagnostic) };
                }
            }

            return status;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Маркер обслуживания {StateFile} повреждён", StateFilePath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Не удалось прочитать маркер обслуживания {StateFile}", StateFilePath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Нет доступа к маркеру обслуживания {StateFile}", StateFilePath);
            return null;
        }
    }

    private static string Limit(string value)
        => value.Length <= 8000 ? value : value[..8000] + "\n…";
}
