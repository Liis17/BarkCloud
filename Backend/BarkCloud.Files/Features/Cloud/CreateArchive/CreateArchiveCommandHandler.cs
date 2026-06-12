using System.IO;
using System.IO.Compression;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.Cloud.CreateArchive;

/// <summary>
/// Собирает ZIP из выбранных источников. Архив пишется во временный файл на диске
/// (Archive:TempPath — обычно отдельный том рядом с MinIO, чтобы не упереться в место образа),
/// затем заливается в S3 как обычный блоб и кладётся СРАЗУ в корзину со сроком 3 дня —
/// переиспользуем фоновую очистку корзины вместо отдельного авто-удаления. Временный файл
/// удаляется после заливки в S3. Клиенту возвращается временная ссылка на скачивание.
/// </summary>
public class CreateArchiveCommandHandler : IRequestHandler<CreateArchiveCommand, CreateArchiveResponse>
{
    /// <summary>Срок жизни архива в корзине до окончательного удаления.</summary>
    private static readonly TimeSpan ArchiveRetention = TimeSpan.FromDays(3);

    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly IUploadedFilesStorage _files;
    private readonly IAlbumStorage _albums;
    private readonly ITempFilesStorage _tempFiles;
    private readonly S3Uploader _s3;
    private readonly S3BucketRegistry _buckets;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreateArchiveCommandHandler> _logger;

    public CreateArchiveCommandHandler(
        ICloudHierarchyStorage hierarchy,
        IUploadedFilesStorage files,
        IAlbumStorage albums,
        ITempFilesStorage tempFiles,
        S3Uploader s3,
        S3BucketRegistry buckets,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<CreateArchiveCommandHandler> logger)
    {
        _hierarchy = hierarchy;
        _files = files;
        _albums = albums;
        _tempFiles = tempFiles;
        _s3 = s3;
        _buckets = buckets;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CreateArchiveResponse> Handle(CreateArchiveCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // 1. Собираем (fileId, путь-в-архиве) из всех источников с проверкой владения.
        var items = new List<(Guid FileId, string Path)>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? suggestedName = request.ArchiveName;

        string Add(Guid fileId, string rawPath)
        {
            var path = UniquePath(rawPath, usedPaths);
            items.Add((fileId, path));
            return path;
        }

        // 1a. Записи иерархии (вкладка «Файлы»).
        if (request.EntryIds.Count > 0)
        {
            var entries = await _hierarchy.GetLiveFileEntriesByIds(ownerId, request.EntryIds, cancellationToken);
            foreach (var e in entries)
                Add(e.FileId, e.Name);
        }

        // 1b. Блобы напрямую (галерея Фото/Видео).
        if (request.FileIds.Count > 0)
        {
            var blobs = await _files.GetFiles(request.FileIds);
            foreach (var f in blobs)
            {
                if (!f.Uploaders.Contains(ownerId))
                    continue; // чужой блоб — не отдаём по знанию id
                Add(f.Id, SafeLeafName(f.Filename, f.Id));
            }
        }

        // 1c. Вся папка рекурсивно (с сохранением структуры подпапок).
        if (request.DirectoryId.HasValue)
        {
            var rootDir = await _hierarchy.GetDirectory(request.DirectoryId.Value, cancellationToken);
            if (rootDir is null)
                throw new DirectoryNotFoundException();
            if (rootDir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            suggestedName ??= rootDir.Name;

            var subtree = await _hierarchy.GetSubtree(ownerId, rootDir.Id, cancellationToken); // включает саму папку
            var dirById = subtree.ToDictionary(d => d.Id);
            var entries = await _hierarchy.GetFileEntriesInDirectories(
                ownerId, subtree.Select(d => d.Id).ToList(), cancellationToken);

            foreach (var e in entries)
            {
                var prefix = RelativeDirPath(e.DirectoryId, rootDir.Id, dirById);
                var path = string.IsNullOrEmpty(prefix) ? e.Name : $"{prefix}/{e.Name}";
                Add(e.FileId, path);
            }
        }

        // 1d. Весь альбом.
        if (request.AlbumId.HasValue)
        {
            var album = await _albums.GetAlbum(request.AlbumId.Value, cancellationToken);
            if (album is null || album.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            suggestedName ??= album.Name;

            var fileIds = new List<Guid>();
            DateTime? cursorAt = null;
            Guid? cursorId = null;
            while (true)
            {
                var page = await _albums.ListItemsPage(album.Id, cursorAt, cursorId, 500, cancellationToken);
                if (page.Count == 0)
                    break;
                fileIds.AddRange(page.Select(i => i.FileId));
                if (page.Count < 500)
                    break;
                cursorAt = page[^1].AddedAt;
                cursorId = page[^1].FileId;
            }

            var blobs = (await _files.GetFiles(fileIds)).ToDictionary(f => f.Id);
            foreach (var fid in fileIds)
                if (blobs.TryGetValue(fid, out var f) && f.Uploaders.Contains(ownerId))
                    Add(f.Id, SafeLeafName(f.Filename, f.Id));
        }

        if (items.Count == 0)
            throw new ArchiveCreationException("Нет доступных файлов для архива.");

        var archiveName = BuildArchiveName(suggestedName);

        // 2. Пишем ZIP во временный файл на диске (вне образа, если задан Archive:TempPath).
        var tempRoot = _configuration["Archive:TempPath"];
        if (string.IsNullOrWhiteSpace(tempRoot))
            tempRoot = Path.GetTempPath();
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"archive-{Guid.NewGuid():N}.zip");
        var bucket = _buckets.GetBucketName(UploadFileType.CloudFile);

        try
        {
            await using (var zipStream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (fileId, path) in items)
                {
                    Stream src;
                    try
                    {
                        src = await _s3.DownloadAsync(bucket, fileId.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Архив: не удалось скачать блоб {FileId} из S3, пропускаем", fileId);
                        continue;
                    }

                    await using (src)
                    {
                        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                        await using var es = entry.Open();
                        await src.CopyToAsync(es, cancellationToken);
                    }
                }
            }

            var size = new FileInfo(tempPath).Length;

            // 3. Заливаем готовый ZIP в S3 как новый блоб.
            var blob = new Domain.UploadFile
            {
                CreatedAt = DateTime.UtcNow,
                Type = UploadFileType.CloudFile,
                Uploaders = new List<long> { ownerId },
                Filename = archiveName,
                MediaKind = archiveName.GetMediaKind(),
            };
            await _files.AddToStorage(blob); // присваивает Id

            await using (var readStream = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous))
            {
                blob.Etag = await _s3.UploadAsync(bucket, blob.Id.ToString(), readStream, "application/zip");
            }

            blob.UploadedAt = DateTime.UtcNow;
            blob.Size = size;
            await _files.UpdateFile(blob);

            // 4. Кладём запись сразу в корзину со сроком 3 дня (фоновая очистка удалит и блоб из S3).
            var now = DateTime.UtcNow;
            await _hierarchy.AddFileEntry(new CloudFileEntry
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                DirectoryId = CloudHierarchyStorage.RootDirectoryId,
                FileId = blob.Id,
                Name = archiveName,
                CreatedAt = now,
                IsDeleted = true,
                DeletedAt = now,
                PurgeAt = now + ArchiveRetention,
            }, cancellationToken);

            // 5. Временная ссылка на немедленное скачивание.
            var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
            var tempFile = await _tempFiles.CreateTempFile(blob.Id);
            var url = FileUrlHelper.GenerateDownloadUrl(baseUrl, tempFile.Id);

            _logger.LogInformation(
                "Архив «{Name}» ({Size} б, файлов {Count}) создан для {Owner}, в корзину до {PurgeAt}",
                archiveName, size, items.Count, ownerId, now + ArchiveRetention);

            return new CreateArchiveResponse
            {
                FileId = blob.Id.ToString(),
                Url = url,
                FileName = archiveName,
            };
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Архив: не удалось удалить временный файл {TempPath}", tempPath);
            }
        }
    }

    /// <summary>Относительный путь директории внутри поддерева root (root → "").</summary>
    private static string RelativeDirPath(Guid dirId, Guid rootId, IReadOnlyDictionary<Guid, CloudDirectory> dirById)
    {
        var parts = new List<string>();
        var cur = dirId;
        // Ограничиваем глубину размером поддерева — страховка от любых аномалий в данных.
        for (var guard = 0; cur != rootId && guard <= dirById.Count; guard++)
        {
            if (!dirById.TryGetValue(cur, out var d))
                break;
            parts.Add(d.Name);
            cur = d.ParentId ?? rootId;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Делает путь уникальным в пределах архива, добавляя « (n)» перед расширением.</summary>
    private static string UniquePath(string rawPath, HashSet<string> used)
    {
        var path = rawPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path))
            path = "file";
        if (used.Add(path))
            return path;

        var slash = path.LastIndexOf('/');
        var dir = slash >= 0 ? path[..slash] : "";
        var leaf = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = leaf.LastIndexOf('.');
        var name = dot > 0 ? leaf[..dot] : leaf;
        var ext = dot > 0 ? leaf[dot..] : "";

        for (var i = 1; ; i++)
        {
            var candidateLeaf = $"{name} ({i}){ext}";
            var candidate = dir.Length > 0 ? $"{dir}/{candidateLeaf}" : candidateLeaf;
            if (used.Add(candidate))
                return candidate;
        }
    }

    /// <summary>Имя файла в архиве по блобу: только имя без пути, фолбэк — id.</summary>
    private static string SafeLeafName(string? filename, Guid fileId)
    {
        var leaf = string.IsNullOrWhiteSpace(filename) ? fileId.ToString() : Path.GetFileName(filename);
        return string.IsNullOrWhiteSpace(leaf) ? fileId.ToString() : leaf;
    }

    private static string BuildArchiveName(string? suggested)
    {
        var baseName = string.IsNullOrWhiteSpace(suggested)
            ? $"Архив {DateTime.UtcNow:yyyy-MM-dd}"
            : suggested.Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');

        if (!baseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            baseName += ".zip";
        return baseName;
    }
}
