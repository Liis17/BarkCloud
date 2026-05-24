using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Альбом — универсальная коллекция фото/видео пользователя поверх блобов.
/// Один и тот же файл может состоять в нескольких альбомах (см. <see cref="AlbumItem"/>),
/// но при этом находиться максимум в одной директории иерархии.
/// </summary>
public class Album
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Владелец альбома.
    /// </summary>
    public long OwnerId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>
    /// Файл-обложка (<see cref="UploadFile"/>). null — обложка берётся из первого элемента.
    /// </summary>
    public Guid? CoverFileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
