using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Грант доступа к файлу (<see cref="UploadFile"/>) конкретному пользователю-получателю.
/// Создаётся владельцем («поделиться с пользователем»); получатель видит файл в разделе
/// «мне доступны» и может его смотреть/скачивать (без редактирования и ре-шаринга).
/// </summary>
public class FileGrant
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец файла, выдавший доступ.</summary>
    public long OwnerId { get; set; }

    /// <summary>Получатель доступа.</summary>
    public long RecipientId { get; set; }

    /// <summary>Идентификатор реального <see cref="UploadFile"/>.</summary>
    public Guid FileId { get; set; }

    /// <summary>Когда выдан доступ.</summary>
    public DateTime CreatedAt { get; set; }
}
