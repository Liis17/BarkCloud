using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.MoveFileEntries;

/// <summary>
/// Массовый перенос записей в одну папку под одной блокировкой дерева — та же семантика,
/// что у единичного <see cref="MoveFileEntry.MoveFileEntryCommand"/>. Ошибка целевой папки
/// отклоняет весь пакет; ошибки отдельных записей (не найдена, конфликт имени) возвращаются
/// по каждой записи и не отменяют перенос остальных.
/// </summary>
public class MoveFileEntriesCommandHandler : IRequestHandler<MoveFileEntriesCommand, MoveFileEntriesResponse>
{
    private static readonly string NotFoundCode = new FileEntryNotFoundException().ErrorCode;
    private static readonly string NameConflictCode = new DirectoryNameConflictException().ErrorCode;

    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<MoveFileEntriesCommandHandler> _logger;

    public MoveFileEntriesCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<MoveFileEntriesCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<MoveFileEntriesResponse> Handle(MoveFileEntriesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var response = new MoveFileEntriesResponse();
        var ids = request.EntryIds.Distinct().ToList();
        if (ids.Count == 0)
            return response;

        await using var treeLock = await _storage.LockTree(ownerId, cancellationToken);

        Guid targetDirectoryId;
        if (request.NewDirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.NewDirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
            targetDirectoryId = dir.Id;
        }
        else
        {
            targetDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        }

        var entries = (await _storage.GetLiveFileEntriesByIds(ownerId, ids, cancellationToken))
            .ToDictionary(entry => entry.Id);
        var candidates = entries.Values.Where(entry => entry.DirectoryId != targetDirectoryId).ToList();
        var takenNames = await _storage.GetLiveFileEntryNames(
            ownerId, targetDirectoryId, candidates.Select(entry => entry.Name).Distinct().ToList(), cancellationToken);

        var moved = new List<(CloudFileEntry Entry, Guid OldDirectoryId)>();
        foreach (var id in ids)
        {
            if (!entries.TryGetValue(id, out var entry))
            {
                response.Failed.Add(new FailedFileEntryMove { EntryId = id.ToString(), ErrorCode = NotFoundCode });
                continue;
            }

            if (entry.DirectoryId == targetDirectoryId)
            {
                response.MovedEntryIds.Add(id.ToString());
                continue;
            }

            // Добавление в takenNames закрывает и конфликт двух записей пакета с одинаковым именем.
            if (!takenNames.Add(entry.Name))
            {
                response.Failed.Add(new FailedFileEntryMove { EntryId = id.ToString(), ErrorCode = NameConflictCode });
                continue;
            }

            moved.Add((entry, entry.DirectoryId));
            entry.DirectoryId = targetDirectoryId;
            response.MovedEntryIds.Add(id.ToString());
        }

        await _storage.SaveChangesAsync(cancellationToken);
        await treeLock.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Массовый перенос в директорию {DirectoryId}: запрошено {RequestedCount}, перенесено {MovedCount}, ошибок {FailedCount} (Owner: {OwnerId})",
            targetDirectoryId, ids.Count, moved.Count, response.Failed.Count, ownerId);

        await _activity.AddManyAsync(moved.Select(item => FileActivityWriter.Create(
            ownerId,
            item.Entry.FileId,
            ownerId,
            FileActivityKind.Moved,
            "Перемещён в другую папку",
            item.Entry.Id,
            new { oldDirectoryId = item.OldDirectoryId, newDirectoryId = targetDirectoryId })),
            cancellationToken);

        return response;
    }
}
