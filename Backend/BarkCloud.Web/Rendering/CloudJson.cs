using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;

namespace BarkCloud.Web.Rendering;

/// <summary>
/// Единый маппинг gRPC-типов Files в JSON-карточки, которые потребляют React-страницы
/// (Фото / Видео / Файлы). Используется и серверным рендером, и /api-эндпоинтами,
/// чтобы у фронта был один формат карточки.
/// </summary>
public static class CloudJson
{
    /// <summary>Карточка файла-блоба: id (он же fileId), имя, тип, размер, превью для srcset.</summary>
    public static object Media(UploadFileInfo f) => Card(f);

    /// <summary>
    /// Карточка элемента галереи: карточка файла + сведения о записях каталога владельца
    /// (entryIds/entryNames нужны фронту для переименования/удаления из галереи).
    /// </summary>
    public static object MediaItem(UserImageItem i)
    {
        var card = Card(i.File);
        card["entriesCount"] = i.EntriesCount;
        card["entryNames"] = i.EntryNames.ToArray();
        card["entryIds"] = i.EntryIds.ToArray();
        return card;
    }

    /// <summary>Базовая карточка блоба в виде словаря (чтобы галерея могла дополнить её entry-полями).</summary>
    private static Dictionary<string, object?> Card(UploadFileInfo f)
    {
        var (iconKind, ext) = FileKind.Classify(f.FileName);

        return new Dictionary<string, object?>
        {
            ["id"] = f.Id,
            ["name"] = f.FileName,
            ["ext"] = ext,
            ["kind"] = MediaKindName(f.MediaKind), // photo / video / document / audio / other
            ["iconKind"] = iconKind,               // img / vid / doc / pdf / zip / code / audio (для Files)
            ["size"] = f.FileSize,
            ["sizeLabel"] = Format.Size(f.FileSize),
            ["width"] = f.ImageWidth,
            ["height"] = f.ImageHeight,
            // массив для <img srcset> — только превью с готовым URL, по возрастанию ширины
            ["previews"] = f.Previews
                .Where(p => !string.IsNullOrEmpty(p.PreviewUrl))
                .OrderBy(p => p.TargetWidth)
                .Select(p => new
                {
                    w = p.ActualWidth > 0 ? p.ActualWidth : p.TargetWidth,
                    target = p.TargetWidth,
                    url = p.PreviewUrl
                })
                .ToArray(),
            ["createdAt"] = Iso(f.CreatedAt),
            ["uploadedAt"] = Iso(f.UploadedAt)
        };
    }

    public static object Dir(DirectoryInfo d) => new
    {
        id = d.Id,
        parentId = d.ParentId,
        name = d.Name,
        createdAt = Iso(d.CreatedAt),
        updatedAt = Iso(d.UpdatedAt)
    };

    public static object Album(AlbumInfo a) => new
    {
        id = a.Id,
        name = a.Name,
        description = a.Description,
        coverFileId = a.CoverFileId,
        coverUrl = a.CoverPreviewUrl,
        count = a.ItemsCount,
        createdAt = Iso(a.CreatedAt),
        updatedAt = Iso(a.UpdatedAt)
    };

    /// <summary>Карточка умной папки: критерии (правила + комбинатор), обложка, счётчик, признак системной.</summary>
    public static object DynamicFolder(DynamicFolderInfo f) => new
    {
        id = f.Id,
        name = f.Name,
        isSystem = f.IsSystem,
        combinator = (int)f.Combinator,
        rules = f.Rules.Select(r => new { field = (int)r.Field, op = (int)r.Operator, value = r.Value }).ToArray(),
        iconKey = f.IconKey,
        coverColor = f.CoverColor,
        coverUrl = f.CoverPreviewUrl,
        count = f.ItemsCount,
        sortOrder = f.SortOrder,
        createdAt = Iso(f.CreatedAt),
        updatedAt = Iso(f.UpdatedAt)
    };

    /// <summary>Запись каталога: метаданные записи + вложенная карточка файла.</summary>
    public static object Entry(FileEntryDetailed e) => new
    {
        entryId = e.Entry.Id,
        fileId = e.Entry.FileId,
        directoryId = e.Entry.DirectoryId,
        name = e.Entry.Name,
        createdAt = Iso(e.Entry.CreatedAt),
        media = e.File is null ? null : Media(e.File)
    };

    /// <summary>Запись в корзине: метаданные + карточка файла + даты удаления/окончательной зачистки.</summary>
    public static object Trash(TrashEntry e) => new
    {
        entryId = e.Entry.Id,
        fileId = e.Entry.FileId,
        name = e.Entry.Name,
        deletedAt = Iso(e.DeletedAt),
        purgeAt = Iso(e.PurgeAt),
        media = e.File is null ? null : Media(e.File)
    };

    private static string MediaKindName(MediaKind kind) => kind switch
    {
        MediaKind.Photo => "photo",
        MediaKind.Video => "video",
        MediaKind.Document => "document",
        MediaKind.Audio => "audio",
        _ => "other"
    };

    private static DateTimeOffset? Iso(Timestamp? ts) => ts?.ToDateTimeOffset();
}
