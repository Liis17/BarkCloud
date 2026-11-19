using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DomainShareLink = BarkCloud.Files.Domain.ShareLink;
using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.Cloud.CreateShare;

public class CreateShareCommandHandler : IRequestHandler<CreateShareCommand, ShareInfo>
{
    private readonly IShareStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<CreateShareCommandHandler> _logger;

    public CreateShareCommandHandler(
        IShareStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<CreateShareCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<ShareInfo> Handle(CreateShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var now = DateTime.UtcNow;
        var expiresAt = ShareExpiry.Validate(request.ExpiresAt, now);

        // Ссылку можно создать только на свой файл.
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null || file.Type != DomainUploadFileType.CloudFile || !file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();
        if (!file.IsReady())
            throw new FileNotReadyException();

        var share = new DomainShareLink
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            FileId = request.FileId,
            Token = ShareExpiry.GenerateToken(),
            Name = request.Name,
            CreatedAt = now,
            ClickCount = 0,
            ExpiresAt = expiresAt
        };

        await _storage.Add(share, cancellationToken);

        _logger.LogInformation("Создана публичная ссылка {ShareId} на файл {FileId} (Owner: {OwnerId})",
            share.Id, request.FileId, ownerId);

        await _activity.AddAsync(
            ownerId,
            request.FileId,
            ownerId,
            FileActivityKind.ShareCreated,
            "Создана публичная ссылка",
            details: new { shareId = share.Id, share.Name },
            cancellationToken: cancellationToken);

        return ToGrpc(share);
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
            ClickCount = share.ClickCount,
            ExpiresAt = ShareExpiry.ToTimestamp(share.ExpiresAt)
        };
    }
}
