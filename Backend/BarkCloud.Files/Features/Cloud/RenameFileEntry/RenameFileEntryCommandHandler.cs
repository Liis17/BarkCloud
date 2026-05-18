using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RenameFileEntry;

public class RenameFileEntryCommandHandler : IRequestHandler<RenameFileEntryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<RenameFileEntryCommandHandler> _logger;

    public RenameFileEntryCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<RenameFileEntryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
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

        entry.Name = newName;
        await _storage.UpdateFileEntry(entry, cancellationToken);

        _logger.LogInformation("Переименована запись {EntryId} в {NewName}", entry.Id, newName);

        return new CloudEmpty();
    }
}
