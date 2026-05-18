using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Папка в иерархии облачного хранилища пользователя.
/// Корневая папка пользователя не материализуется — её представляет ParentId == null.
/// </summary>
public class CloudDirectory
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Владелец папки.
    /// </summary>
    public long OwnerId { get; set; }

    /// <summary>
    /// Идентификатор родительской папки. null означает, что папка лежит в корне владельца.
    /// </summary>
    public Guid? ParentId { get; set; }

    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
