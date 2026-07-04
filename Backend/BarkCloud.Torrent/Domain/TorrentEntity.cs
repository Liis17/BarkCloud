using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Torrent.Domain;

/// <summary>
/// Торрент пользователя. Строка в БД переживает рестарт сервиса: по MagnetUri/TorrentFile
/// торрент пере-добавляется в движок, накопленная статистика (Downloaded/Uploaded) сохраняется.
/// </summary>
public class TorrentEntity
{
    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }

    public string InfoHash { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Magnet-ссылка, если торрент добавлен по ней (иначе null → используется TorrentFile).</summary>
    public string? MagnetUri { get; set; }

    /// <summary>Содержимое .torrent (если добавлен файлом) — чтобы пере-добавить при рестарте.</summary>
    public byte[]? TorrentFile { get; set; }

    /// <summary>Папка на диске, куда качается торрент ({DownloadPath}/{userId}).</summary>
    public string SavePath { get; set; } = "";

    /// <summary>Значение <see cref="BarkCloud.Proto.Torrent.TorrentStatus"/>.</summary>
    public int Status { get; set; }

    public long TotalSize { get; set; }

    public long Downloaded { get; set; }

    public long Uploaded { get; set; }

    public double Progress { get; set; }

    /// <summary>true — пользователь приостановил торрент (не возобновлять авто-при рестарте).</summary>
    public bool Paused { get; set; }

    public DateTime AddedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public List<TorrentFileEntity> Files { get; set; } = new();
}
