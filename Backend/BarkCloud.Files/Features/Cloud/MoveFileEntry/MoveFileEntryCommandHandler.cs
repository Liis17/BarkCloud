using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.MoveFileEntry;

public class MoveFileEntryCommandHandler : IRequestHandler<MoveFileEntryCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<MoveFileEntryCommandHandler> _logger;

    public MoveFileEntryCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<MoveFileEntryCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(MoveFileEntryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entry = await _storage.GetFileEntry(request.EntryId, cancellationToken);
        if (entry is null)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        Guid newStorageDirectoryId;
        if (request.NewDirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.NewDirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
            newStorageDirectoryId = dir.Id;
        }
        else
        {
            newStorageDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        }

        if (entry.DirectoryId == newStorageDirectoryId)
            return new CloudEmpty();

        if (await _storage.FileEntryNameExists(ownerId, newStorageDirectoryId, entry.Name, cancellationToken))
            throw new DirectoryNameConflictException();

        var oldDirectoryId = entry.DirectoryId;
        entry.DirectoryId = newStorageDirectoryId;
        await _storage.UpdateFileEntry(entry, cancellationToken);

        _logger.LogInformation(
            "Перемещена запись {EntryId} в директорию {DirectoryId}",
            entry.Id, newStorageDirectoryId);

        await _activity.AddAsync(
            ownerId,
            entry.FileId,
            ownerId,
            FileActivityKind.Moved,
            "Перемещён в другую папку",
            entry.Id,
            new { oldDirectoryId, newDirectoryId = newStorageDirectoryId },
            cancellationToken);

        return new CloudEmpty();
    }
}
