using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.CopyFileEntry;

public class CopyFileEntryCommandHandler : IRequestHandler<CopyFileEntryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<CopyFileEntryCommandHandler> _logger;

    public CopyFileEntryCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<CopyFileEntryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(CopyFileEntryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var source = await _storage.GetFileEntry(request.SourceEntryId, cancellationToken);
        if (source is null)
            throw new FileEntryNotFoundException();
        if (source.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        Guid targetDirectoryId;
        if (request.TargetDirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.TargetDirectoryId.Value, cancellationToken);
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

        var name = (request.NewName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = source.Name;

        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryNameConflictException();

        if (targetDirectoryId == source.DirectoryId && name == source.Name)
            throw new DirectoryNameConflictException();

        if (await _storage.FileEntryNameExists(ownerId, targetDirectoryId, name, cancellationToken))
            throw new DirectoryNameConflictException();

        var copy = new CloudFileEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            DirectoryId = targetDirectoryId,
            FileId = source.FileId,
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        await _storage.AddFileEntry(copy, cancellationToken);

        _logger.LogInformation(
            "Скопирована запись {SourceId} → {NewEntryId} (FileId: {FileId}, TargetDir: {Dir}, Name: {Name}, Owner: {Owner})",
            source.Id, copy.Id, copy.FileId, targetDirectoryId, name, ownerId);

        return new CloudEmpty();
    }
}
