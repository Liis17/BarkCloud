using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Запись (link) о привязке существующего <see cref="UploadFile"/> к директории
/// в иерархии облачного хранилища пользователя.
/// </summary>
public class CloudFileEntry
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Владелец записи (тот, кому принадлежит привязка в иерархии).
    /// </summary>
    public long OwnerId { get; set; }

    /// <summary>
    /// Директория, в которой лежит запись.
    /// </summary>
    public Guid DirectoryId { get; set; }

    /// <summary>
    /// Идентификатор реального <see cref="UploadFile"/>.
    /// </summary>
    public Guid FileId { get; set; }

    /// <summary>
    /// Отображаемое имя файла в иерархии (не меняет UploadFile.Filename).
    /// </summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Запись находится в корзине (мягко удалена). Такие записи исключаются из всех
    /// «живых» выборок (иерархия, галерея, альбомы) и из частичных уникальных индексов.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Когда запись была перемещена в корзину (null, пока не удалена).
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Когда запись будет окончательно удалена фоновым воркером (DeletedAt + срок хранения).
    /// </summary>
    public DateTime? PurgeAt { get; set; }
}
