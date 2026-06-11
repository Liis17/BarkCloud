using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RemoveFavorite;

public class RemoveFavoriteCommandHandler : IRequestHandler<RemoveFavoriteCommand, CloudEmpty>
{
    private readonly IFavoriteFilesStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RemoveFavoriteCommandHandler> _logger;

    public RemoveFavoriteCommandHandler(
        IFavoriteFilesStorage storage,
        UserContext userContext,
        ILogger<RemoveFavoriteCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RemoveFavoriteCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Удаляем только строку этого владельца — идемпотентно, без ошибки если файла не было в избранном.
        var removed = await _storage.Remove(ownerId, request.FileId, cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("Файл {FileId} убран из избранного (Owner: {OwnerId})", request.FileId, ownerId);
            await _activity.AddAsync(
                ownerId,
                request.FileId,
                ownerId,
                FileActivityKind.FavoriteRemoved,
                "Убран из избранного",
                cancellationToken: cancellationToken);
        }

        return new CloudEmpty();
    }
}
