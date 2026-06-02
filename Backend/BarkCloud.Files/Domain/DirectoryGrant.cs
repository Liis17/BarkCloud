using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Грант доступа к папке (<see cref="CloudDirectory"/>) конкретному пользователю-получателю.
/// Создаётся владельцем («поделиться папкой с пользователем»); получатель видит папку в разделе
/// «мне доступны», листает её рекурсивно (включая подпапки и файлы, добавленные позже)
/// и может смотреть/скачивать файлы (без редактирования и ре-шаринга).
/// </summary>
public class DirectoryGrant
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец папки, выдавший доступ.</summary>
    public long OwnerId { get; set; }

    /// <summary>Получатель доступа.</summary>
    public long RecipientId { get; set; }

    /// <summary>Идентификатор расшаренной <see cref="CloudDirectory"/> (корень доступного поддерева).</summary>
    public Guid DirectoryId { get; set; }

    /// <summary>Когда выдан доступ.</summary>
    public DateTime CreatedAt { get; set; }
}
