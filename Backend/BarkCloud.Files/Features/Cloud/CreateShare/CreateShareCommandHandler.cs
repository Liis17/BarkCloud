using System.Security.Cryptography;

using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DomainShareLink = BarkCloud.Files.Domain.ShareLink;

namespace BarkCloud.Files.Features.Cloud.CreateShare;

public class CreateShareCommandHandler : IRequestHandler<CreateShareCommand, ShareInfo>
{
    private readonly IShareStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateShareCommandHandler> _logger;

    public CreateShareCommandHandler(
        IShareStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<CreateShareCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<ShareInfo> Handle(CreateShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Ссылку можно создать только на свой файл.
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null || !file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        var share = new DomainShareLink
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            FileId = request.FileId,
            Token = GenerateToken(),
            Name = request.Name,
            CreatedAt = DateTime.UtcNow,
            ClickCount = 0
        };

        await _storage.Add(share, cancellationToken);

        _logger.LogInformation("Создана публичная ссылка {ShareId} на файл {FileId} (Owner: {OwnerId})",
            share.Id, request.FileId, ownerId);

        return ToGrpc(share);
    }

    /// <summary>URL-safe токен из 16 случайных байт (base64url без паддинга).</summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static ShareInfo ToGrpc(DomainShareLink share)
    {
        return new ShareInfo
        {
            Id = share.Id.ToString(),
            Token = share.Token,
            FileId = share.FileId.ToString(),
            Name = share.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(share.CreatedAt, DateTimeKind.Utc)),
            ClickCount = share.ClickCount
        };
    }
}
