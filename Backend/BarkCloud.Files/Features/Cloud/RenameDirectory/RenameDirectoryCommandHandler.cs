using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.RenameDirectory;

public class RenameDirectoryCommandHandler : IRequestHandler<RenameDirectoryCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<RenameDirectoryCommandHandler> _logger;

    public RenameDirectoryCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<RenameDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RenameDirectoryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var newName = (request.NewName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new DirectoryNameConflictException();

        var directory = await _storage.GetDirectory(request.DirectoryId, cancellationToken);
        if (directory is null)
            throw new DirectoryNotFoundException();
        if (directory.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        if (directory.Name == newName)
            return new CloudEmpty();

        if (await _storage.DirectoryNameExists(ownerId, directory.ParentId, newName, cancellationToken))
            throw new DirectoryNameConflictException();

        directory.Name = newName;
        directory.UpdatedAt = DateTime.UtcNow;
        await _storage.UpdateDirectory(directory, cancellationToken);

        _logger.LogInformation("Переименована папка {DirectoryId} в {NewName}", directory.Id, newName);

        return new CloudEmpty();
    }
}
