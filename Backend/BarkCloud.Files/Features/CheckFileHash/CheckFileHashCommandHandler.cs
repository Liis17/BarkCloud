using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

using System.Text.RegularExpressions;

namespace BarkCloud.Files.Features.CheckFileHash;

public partial class CheckFileHashCommandHandler : IRequestHandler<CheckFileHashCommand, CheckFileHashResponse>
{
    private readonly IFileHashesStorage _hashesStorage;
    private readonly ICloudHierarchyStorage _hierarchyStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<CheckFileHashCommandHandler> _logger;

    // Regex for validating SHA256 hash format (64 hex characters)
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HashRegex();

    public CheckFileHashCommandHandler(
        IFileHashesStorage hashesStorage,
        ICloudHierarchyStorage hierarchyStorage,
        UserContext userContext,
        ILogger<CheckFileHashCommandHandler> logger)
    {
        _hashesStorage = hashesStorage;
        _hierarchyStorage = hierarchyStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CheckFileHashResponse> Handle(CheckFileHashCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Проверка хеша файла: {FileHash}", request.FileHash);

        // Validate hash format (must be 64 hex characters for SHA256)
        if (string.IsNullOrEmpty(request.FileHash) || !Sha256HashRegex().IsMatch(request.FileHash))
        {
            _logger.LogWarning("Неверный формат хеша: {FileHash}", request.FileHash);
            return new CheckFileHashResponse
            {
                FileId = string.Empty
            };
        }

        // Normalize hash to lowercase
        var normalizedHash = request.FileHash.ToLowerInvariant();

        // Дедуп снят: контент с одним хешем может относиться к нескольким блобам.
        var fileIds = await _hashesStorage.GetFileIdsByHash(normalizedHash, cancellationToken);
        if (fileIds.Count == 0)
        {
            _logger.LogInformation("Файл с хешем {FileHash} не найден", normalizedHash);
            return new CheckFileHashResponse { FileId = string.Empty, Exists = false };
        }

        // Наличие определяем ТОЛЬКО по файлам текущего пользователя (его живым записям в облаке).
        // Глобальное присутствие хеша не раскрываем — иначе ответ палил бы наличие контента у
        // других пользователей. AddUploaderToFile здесь намеренно НЕ вызывается: проверка без
        // побочных эффектов, решение «грузить копию / открыть существующий» принимает клиент.
        var ownerId = _userContext.UserId;
        var entries = await _hierarchyStorage.GetLiveEntriesForFiles(ownerId, fileIds, cancellationToken);
        if (entries.Count == 0)
        {
            _logger.LogInformation("Файл с хешем {FileHash} у пользователя {Owner} не найден", normalizedHash, ownerId);
            return new CheckFileHashResponse { FileId = string.Empty, Exists = false };
        }

        _logger.LogInformation("Файл с хешем {FileHash} найден у пользователя {Owner} ({Count} запис(ей))",
            normalizedHash, ownerId, entries.Count);

        var response = new CheckFileHashResponse
        {
            FileId = entries[0].FileId.ToString(),
            Exists = true
        };

        // Локации существующих копий пользователя (имя + папка) — для модалки «файл уже есть».
        foreach (var entry in entries)
        {
            var isRoot = entry.DirectoryId == CloudHierarchyStorage.RootDirectoryId;
            var directoryName = isRoot
                ? string.Empty
                : (await _hierarchyStorage.GetDirectoryAsNoTracking(entry.DirectoryId, cancellationToken))?.Name ?? string.Empty;

            response.ExistingLocations.Add(new ExistingLocation
            {
                EntryId = entry.Id.ToString(),
                Name = entry.Name,
                DirectoryId = isRoot ? string.Empty : entry.DirectoryId.ToString(),
                DirectoryName = directoryName
            });
        }

        return response;
    }
}
