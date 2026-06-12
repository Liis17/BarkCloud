using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;
using MediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.AttachFile;

public class AttachFileCommandHandler : IRequestHandler<AttachFileCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<AttachFileCommandHandler> _logger;

    public AttachFileCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<AttachFileCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(AttachFileCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryNameConflictException();

        // Валидируем директорию (если указана). При route_by_media_kind папка определяется
        // ниже по типу медиа, поэтому присланный directory_id игнорируется.
        Guid storageDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        if (!request.RouteByMediaKind && request.DirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.DirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
            storageDirectoryId = dir.Id;
        }

        // Проверяем существование UploadFile
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null)
            throw new FileNotFoundException();

        // Разрешаем привязку только если пользователь уже в Uploaders (то есть он загружал файл).
        // Иначе кто угодно мог бы «приватизировать» чужой файл по знанию его ID.
        if (!file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        // Авто-распределение по типу медиа в системные папки Фото/Видео/Другие документы
        // (применяется, когда клиент грузит без явной папки — вкладки Фото/Видео, общий аплоад).
        if (request.RouteByMediaKind)
        {
            var (systemKind, folderName) = MapMediaKindToSystemFolder(file.MediaKind);
            storageDirectoryId = await _storage.EnsureSystemDirectory(ownerId, systemKind, folderName, cancellationToken);
        }

        // Инвариант «одна директория на файл»: блоб владельца может быть привязан только к одной директории.
        if (await _storage.FileEntryExistsForFile(ownerId, file.Id, cancellationToken))
            throw new FileAlreadyAttachedException();

        // Коллизия имени в директории разрешается авто-переименованием: новый файл
        // получает суффикс " (1)", " (2)"… вместо отклонения ошибкой.
        name = await UniqueNameResolver.ResolveAsync(
            name,
            (candidate, ct) => _storage.FileEntryNameExists(ownerId, storageDirectoryId, candidate, ct),
            cancellationToken);

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

        await _activity.AddAsync(
            ownerId,
            file.Id,
            ownerId,
            FileActivityKind.Attached,
            $"Добавлен в папку как «{name}»",
            entry.Id,
            new { directoryId = storageDirectoryId, name },
            cancellationToken);

        return new CloudEmpty();
    }

    private static (CloudDirectorySystemKind kind, string name) MapMediaKindToSystemFolder(MediaKind mediaKind) => mediaKind switch
    {
        MediaKind.Photo => (CloudDirectorySystemKind.Photos, "Фото"),
        MediaKind.Video => (CloudDirectorySystemKind.Videos, "Видео"),
        _ => (CloudDirectorySystemKind.OtherDocuments, "Другие документы"),
    };
}
