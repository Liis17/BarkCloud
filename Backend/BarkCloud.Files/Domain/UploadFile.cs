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

    /// <summary>
    /// Имя устройства, с которого файл был загружен (читается из gRPC-заголовка x-device-name
    /// в момент создания записи в RPC GetUploadUrl). При дедупликации блоба сохраняется значение
    /// первой успешной загрузки этого контента.
    /// </summary>
    public string? UploadDeviceName { get; set; }

    public long Size { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }
}