using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Трек в музыкальном плейлисте. Порядок задаёт владелец плейлиста.
/// </summary>
public class MusicPlaylistItem
{
    [Key]
    public Guid Id { get; set; }

    public Guid PlaylistId { get; set; }

    public Guid FileId { get; set; }

    public long OwnerId { get; set; }

    public int Position { get; set; }

    public DateTime AddedAt { get; set; }
}
