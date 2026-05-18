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
}
