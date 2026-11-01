using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
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
    private readonly IFileHashesStorage _hashesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<CheckFileHashesCommandHandler> _logger;

    // Ограничение на размер пакета, чтобы один запрос не разрастался.
    private const int MaxBatchSize = 500;

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HashRegex();

    public CheckFileHashesCommandHandler(
        IFileHashesStorage hashesStorage,
        UserContext userContext,
        ILogger<CheckFileHashesCommandHandler> logger)
    {
        _hashesStorage = hashesStorage;
        _userContext = userContext;
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

        // Только хеши, файлы которых есть у текущего пользователя — иначе раскрыли бы наличие чужих файлов.
        var matches = await _hashesStorage.GetLiveHashMatchesForOwner(_userContext.UserId, normalized, cancellationToken);
        var matchesByHash = matches.GroupBy(m => m.Hash).ToDictionary(g => g.Key, g => g.ToList());

        var response = new CheckFileHashesResponse();
        foreach (var hash in normalized)
        {
            var result = new HashCheckResult { FileHash = hash, Exists = matchesByHash.ContainsKey(hash) };
            if (matchesByHash.TryGetValue(hash, out var hashMatches))
            {
                // Только READY-блобы; порядок фиксирован, чтобы клиент детерминированно выбирал ID.
                result.FileIds.AddRange(hashMatches
                    .Where(m => m.IsReady)
                    .Select(m => m.FileId.ToString())
                    .Distinct()
                    .Order(StringComparer.Ordinal));
            }

            response.Results.Add(result);
        }

        return response;
    }
}
