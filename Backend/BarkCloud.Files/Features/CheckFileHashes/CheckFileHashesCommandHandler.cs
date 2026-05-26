using BarkCloud.Files.Persistence;
using BarkCloud.Proto.Files;

using MediatR;

using System.Text.RegularExpressions;

namespace BarkCloud.Files.Features.CheckFileHashes;

/// <summary>
/// Пакетная проверка наличия файлов по списку SHA256-хешей. В отличие от
/// <c>CheckFileHash</c>, НЕ имеет побочных эффектов (не добавляет пользователя в
/// uploaders) — предназначена для пассивной индикации «уже в облаке» в UI.
/// </summary>
public partial class CheckFileHashesCommandHandler : IRequestHandler<CheckFileHashesCommand, CheckFileHashesResponse>
{
    private readonly FileHashesStorage _hashesStorage;
    private readonly ILogger<CheckFileHashesCommandHandler> _logger;

    // Ограничение на размер пакета, чтобы один запрос не разрастался.
    private const int MaxBatchSize = 500;

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HashRegex();

    public CheckFileHashesCommandHandler(
        FileHashesStorage hashesStorage,
        ILogger<CheckFileHashesCommandHandler> logger)
    {
        _hashesStorage = hashesStorage;
        _logger = logger;
    }

    public async Task<CheckFileHashesResponse> Handle(CheckFileHashesCommand request, CancellationToken cancellationToken)
    {
        // Нормализуем, отбрасываем некорректные и дубли, сохраняя порядок.
        var normalized = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in request.FileHashes)
        {
            if (string.IsNullOrEmpty(raw) || !Sha256HashRegex().IsMatch(raw))
                continue;

            var hash = raw.ToLowerInvariant();
            if (seen.Add(hash))
                normalized.Add(hash);

            if (normalized.Count >= MaxBatchSize)
                break;
        }

        _logger.LogInformation("Пакетная проверка хешей: получено {Total}, валидных уникальных {Valid}",
            request.FileHashes.Count, normalized.Count);

        var existing = await _hashesStorage.GetExistingHashes(normalized);

        var response = new CheckFileHashesResponse();
        foreach (var hash in normalized)
        {
            response.Results.Add(new HashCheckResult
            {
                FileHash = hash,
                Exists = existing.Contains(hash)
            });
        }

        return response;
    }
}
