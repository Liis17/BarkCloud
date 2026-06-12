using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RenameFileEntry;

public class RenameFileEntryCommandHandler : IRequestHandler<RenameFileEntryCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RenameFileEntryCommandHandler> _logger;

    public RenameFileEntryCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<RenameFileEntryCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RenameFileEntryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var newName = (request.NewName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new DirectoryNameConflictException();

        var entry = await _storage.GetFileEntry(request.EntryId, cancellationToken);
        if (entry is null)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        if (entry.Name == newName)
            return new CloudEmpty();

        if (await _storage.FileEntryNameExists(ownerId, entry.DirectoryId, newName, cancellationToken))
            throw new DirectoryNameConflictException();

        var oldName = entry.Name;
        entry.Name = newName;
        await _storage.UpdateFileEntry(entry, cancellationToken);

        _logger.LogInformation("Переименована запись {EntryId} в {NewName}", entry.Id, newName);

        await _activity.AddAsync(
            ownerId,
            entry.FileId,
            ownerId,
            FileActivityKind.Renamed,
            $"Переименован: «{oldName}» → «{newName}»",
            entry.Id,
            new { oldName, newName },
            cancellationToken);

        return new CloudEmpty();
    }
}
