using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ShareFileWithUser;

public class ShareFileWithUserCommandHandler : IRequestHandler<ShareFileWithUserCommand, CloudEmpty>
{
    private readonly IGrantStorage _grantStorage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<ShareFileWithUserCommandHandler> _logger;

    public ShareFileWithUserCommandHandler(
        IGrantStorage grantStorage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<ShareFileWithUserCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _grantStorage = grantStorage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(ShareFileWithUserCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Поделиться можно только своим файлом.
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null || !file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        // Шаринг самому себе бессмыслен — тихо игнорируем (веб-поиск получателей и так исключает себя).
        if (request.RecipientUserId == ownerId)
            return new CloudEmpty();

        // Идемпотентность: если грант уже есть — ничего не делаем.
        if (await _grantStorage.Exists(ownerId, request.FileId, request.RecipientUserId, cancellationToken))
            return new CloudEmpty();

        var grant = new FileGrant
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            RecipientId = request.RecipientUserId,
            FileId = request.FileId,
            CreatedAt = DateTime.UtcNow
        };
        await _grantStorage.Add(grant, cancellationToken);

        _logger.LogInformation(
            "Файл {FileId} расшарен пользователю {RecipientId} (Owner: {OwnerId})",
            request.FileId, request.RecipientUserId, ownerId);

        await _activity.AddAsync(
            ownerId,
            request.FileId,
            ownerId,
            FileActivityKind.SharedWithUser,
            "Выдан доступ пользователю",
            details: new { grantId = grant.Id, recipientUserId = request.RecipientUserId },
            cancellationToken: cancellationToken);

        return new CloudEmpty();
    }
}
