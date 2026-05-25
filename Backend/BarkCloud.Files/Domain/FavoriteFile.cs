using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Отметка «избранное» для файла (<see cref="UploadFile"/>) на уровне пользователя.
/// Привязка к блобу, а не к записи иерархии, поэтому покрывает и фото/видео из галереи
/// (у которых может не быть <see cref="CloudFileEntry"/>), и файлы/документы из папок.
/// Наличие строки = файл в избранном; уникальность по (OwnerId, FileId).
/// </summary>
public class FavoriteFile
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец избранного.</summary>
    public long OwnerId { get; set; }

    /// <summary>Идентификатор реального <see cref="UploadFile"/> (фото / видео / документ).</summary>
    public Guid FileId { get; set; }

    /// <summary>Когда файл добавлен в избранное (для сортировки списка).</summary>
    public DateTime CreatedAt { get; set; }
}
