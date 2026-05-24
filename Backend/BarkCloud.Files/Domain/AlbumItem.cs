using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Привязка файла (<see cref="UploadFile"/>) к альбому. Реализует связь many-to-many:
/// один файл может состоять в нескольких альбомах.
/// </summary>
public class AlbumItem
{
    [Key]
    public Guid Id { get; set; }

    public Guid AlbumId { get; set; }

    /// <summary>
    /// Идентификатор реального <see cref="UploadFile"/> (фото или видео).
    /// </summary>
    public Guid FileId { get; set; }

    /// <summary>
    /// Владелец (дублируется из альбома для быстрых проверок/фильтрации).
    /// </summary>
    public long OwnerId { get; set; }

    public DateTime AddedAt { get; set; }
}
