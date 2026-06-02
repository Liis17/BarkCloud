using System.Security.Cryptography;

using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DomainFolderShareLink = BarkCloud.Files.Domain.FolderShareLink;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.CreateFolderShare;

public class CreateFolderShareCommandHandler : IRequestHandler<CreateFolderShareCommand, FolderShareInfo>
{
    private readonly IFolderShareStorage _storage;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateFolderShareCommandHandler> _logger;

    public CreateFolderShareCommandHandler(
        IFolderShareStorage storage,
        ICloudHierarchyStorage hierarchy,
        UserContext userContext,
        ILogger<CreateFolderShareCommandHandler> logger)
    {
        _storage = storage;
        _hierarchy = hierarchy;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<FolderShareInfo> Handle(CreateFolderShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Публиковать можно только свою папку.
        var dir = await _hierarchy.GetDirectoryAsNoTracking(request.DirectoryId, cancellationToken);
        if (dir is null)
            throw new DirectoryNotFoundException();
        if (dir.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Идемпотентность: один публичный шар на папку — вернём существующий.
        var existing = await _storage.GetByDirectory(ownerId, request.DirectoryId, cancellationToken);
        if (existing is not null)
            return ToGrpc(existing);

        var share = new DomainFolderShareLink
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            DirectoryId = request.DirectoryId,
            Token = GenerateToken(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? dir.Name : request.Name,
            CreatedAt = DateTime.UtcNow,
            ClickCount = 0
        };

        await _storage.Add(share, cancellationToken);

        _logger.LogInformation("Создана публичная папка {ShareId} на директорию {DirectoryId} (Owner: {OwnerId})",
            share.Id, request.DirectoryId, ownerId);

        return ToGrpc(share);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static FolderShareInfo ToGrpc(DomainFolderShareLink share)
    {
        return new FolderShareInfo
        {
            Id = share.Id.ToString(),
            Token = share.Token,
            DirectoryId = share.DirectoryId.ToString(),
            Name = share.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(share.CreatedAt, DateTimeKind.Utc)),
            ClickCount = share.ClickCount
        };
    }
}
