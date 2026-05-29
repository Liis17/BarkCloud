using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DomainFavoriteFile = BarkCloud.Files.Domain.FavoriteFile;

namespace BarkCloud.Files.Features.Cloud.AddFavorite;

public class AddFavoriteCommandHandler : IRequestHandler<AddFavoriteCommand, CloudEmpty>
{
    private readonly IFavoriteFilesStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<AddFavoriteCommandHandler> _logger;

    public AddFavoriteCommandHandler(
        IFavoriteFilesStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<AddFavoriteCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(AddFavoriteCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Принимаем только файл, принадлежащий пользователю (защита от «избранного» чужого файла по ID).
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null || !file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        // Идемпотентность: повторное добавление не создаёт дубль.
        if (await _storage.Exists(ownerId, request.FileId, cancellationToken))
            return new CloudEmpty();

        await _storage.Add(new DomainFavoriteFile
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            FileId = request.FileId,
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        _logger.LogInformation("Файл {FileId} добавлен в избранное (Owner: {OwnerId})", request.FileId, ownerId);

        return new CloudEmpty();
    }
}
