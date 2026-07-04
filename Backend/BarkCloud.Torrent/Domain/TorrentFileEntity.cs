using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Torrent.Domain;

/// <summary>
/// Файл внутри торрента. Приоритет хранится в БД, чтобы восстанавливать выбор
/// «качать/не качать» при пере-добавлении торрента после рестарта.
/// </summary>
public class TorrentFileEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid TorrentId { get; set; }

    public TorrentEntity Torrent { get; set; } = null!;

    public int Index { get; set; }

    public string Path { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Значение <see cref="BarkCloud.Proto.Torrent.TorrentFilePriority"/>.</summary>
    public int Priority { get; set; }
}
