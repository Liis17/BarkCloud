using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.CreateDirectory;

public class CreateDirectoryCommandHandler : IRequestHandler<CreateDirectoryCommand, DirectoryInfo>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateDirectoryCommandHandler> _logger;

    public CreateDirectoryCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<CreateDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<DirectoryInfo> Handle(CreateDirectoryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var name = (request.Name ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryNameConflictException();

        // Если указан родитель — он должен существовать и принадлежать пользователю
        if (request.ParentId.HasValue)
        {
            var parent = await _storage.GetDirectoryAsNoTracking(request.ParentId.Value, cancellationToken);
            if (parent is null)
                throw new DirectoryNotFoundException();
            if (parent.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
        }

        if (await _storage.DirectoryNameExists(ownerId, request.ParentId, name, cancellationToken))
            throw new DirectoryNameConflictException();

        var now = DateTime.UtcNow;
        var directory = new CloudDirectory
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ParentId = request.ParentId,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _storage.AddDirectory(directory, cancellationToken);

        _logger.LogInformation(
            "Создана папка {DirectoryId} (Name: {Name}, Parent: {ParentId}, Owner: {OwnerId})",
            directory.Id, directory.Name, directory.ParentId, ownerId);

        return new DirectoryInfo
        {
            Id = directory.Id.ToString(),
            ParentId = directory.ParentId?.ToString() ?? string.Empty,
            Name = directory.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.UpdatedAt, DateTimeKind.Utc))
        };
    }
}
