using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Музыкальный плейлист пользователя поверх аудио-блобов.
/// </summary>
public class MusicPlaylist
{
    [Key]
    public Guid Id { get; set; }

    public long OwnerId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>
    /// Кастомная обложка из фото владельца. null — берём обложку первого трека.
    /// </summary>
    public Guid? CoverFileId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
