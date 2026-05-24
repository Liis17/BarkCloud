using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

public class UploadFile
{

    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// List of user IDs who uploaded this file (for deduplication tracking).
    /// </summary>
    public List<long> Uploaders { get; set; } = new();

    public DateTime CreatedAt { get; set; }

    public DateTime? UploadedAt { get; set; }

    public string? Etag { get; set; }

    public UploadFileType Type { get; set; }

    /// <summary>
    /// Категория медиа-контента (фото / видео / документ / аудио). Заполняется при загрузке
    /// по content-type. Используется для галереи и альбомов.
    /// </summary>
    public MediaKind MediaKind { get; set; }

    public string? Filename { get; set; }

    public long Size { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }
}