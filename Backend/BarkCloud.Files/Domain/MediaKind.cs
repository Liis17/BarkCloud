namespace BarkCloud.Files.Domain;

/// <summary>
/// Категория медиа-контента файла. Определяется при загрузке по content-type
/// и используется для фильтрации галереи (фото / видео) и наполнения альбомов.
/// </summary>
public enum MediaKind
{
    Other = 0,
    Photo = 1,
    Video = 2,
    Document = 3,
    Audio = 4
}
