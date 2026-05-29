using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;

namespace BarkCloud.Files.Features.Cloud.AttachFile;

public class AttachFileCommandHandler : IRequestHandler<AttachFileCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<AttachFileCommandHandler> _logger;

    public AttachFileCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<AttachFileCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(AttachFileCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryNameConflictException();

        // Валидируем директорию (если указана)
        Guid storageDirectoryId;
        if (request.DirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.DirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
            storageDirectoryId = dir.Id;
        }
        else
        {
            storageDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        }

        // Проверяем существование UploadFile
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null)
            throw new FileNotFoundException();

        // Разрешаем привязку только если пользователь уже в Uploaders (то есть он загружал файл).
        // Иначе кто угодно мог бы «приватизировать» чужой файл по знанию его ID.
        if (!file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        // Инвариант «одна директория на файл»: блоб владельца может быть привязан только к одной директории.
        if (await _storage.FileEntryExistsForFile(ownerId, file.Id, cancellationToken))
            throw new FileAlreadyAttachedException();

        // Проверяем уникальность имени в директории
        if (await _storage.FileEntryNameExists(ownerId, storageDirectoryId, name, cancellationToken))
            throw new DirectoryNameConflictException();

        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            DirectoryId = storageDirectoryId,
            FileId = file.Id,
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        await _storage.AddFileEntry(entry, cancellationToken);

        _logger.LogInformation(
            "Файл {FileId} привязан к директории {DirectoryId} как {EntryId} (Name: {Name}, Owner: {OwnerId})",
            file.Id, storageDirectoryId, entry.Id, name, ownerId);

        return new CloudEmpty();
    }
}
