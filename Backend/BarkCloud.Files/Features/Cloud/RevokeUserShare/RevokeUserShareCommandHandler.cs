using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeUserShare;

public class RevokeUserShareCommandHandler : IRequestHandler<RevokeUserShareCommand, CloudEmpty>
{
    private readonly IGrantStorage _grantStorage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RevokeUserShareCommandHandler> _logger;

    public RevokeUserShareCommandHandler(
        IGrantStorage grantStorage,
        UserContext userContext,
        ILogger<RevokeUserShareCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _grantStorage = grantStorage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeUserShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Удаляем только грант этого владельца — идемпотентно, без ошибки если его уже нет / не его.
        var grant = await _grantStorage.GetById(request.GrantId, cancellationToken);
        var removed = await _grantStorage.Remove(ownerId, request.GrantId, cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("Отозван грант доступа {GrantId} (Owner: {OwnerId})", request.GrantId, ownerId);
            if (grant is not null)
            {
                await _activity.AddAsync(
                    ownerId,
                    grant.FileId,
                    ownerId,
                    FileActivityKind.UserShareRevoked,
                    "Доступ пользователя отозван",
                    details: new { grantId = grant.Id, recipientUserId = grant.RecipientId },
                    cancellationToken: cancellationToken);
            }
        }

        return new CloudEmpty();
    }
}
