using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Публичная ссылка на музыкальный плейлист.
/// </summary>
public class MusicPlaylistShareLink
{
    [Key]
    public Guid Id { get; set; }

    public long OwnerId { get; set; }

    public Guid PlaylistId { get; set; }

    public string Token { get; set; } = "";

    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public long ClickCount { get; set; }
}
