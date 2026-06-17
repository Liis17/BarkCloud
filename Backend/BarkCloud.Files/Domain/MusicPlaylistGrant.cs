using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Приватный доступ конкретного пользователя к музыкальному плейлисту.
/// </summary>
public class MusicPlaylistGrant
{
    [Key]
    public Guid Id { get; set; }

    public long OwnerId { get; set; }

    public long RecipientId { get; set; }

    public Guid PlaylistId { get; set; }

    public DateTime CreatedAt { get; set; }
}
