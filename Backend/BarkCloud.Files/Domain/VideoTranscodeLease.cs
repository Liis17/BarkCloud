using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Единственная аренда, разрешающая одному экземпляру Files обрабатывать очередь видео.
/// </summary>
public sealed class VideoTranscodeLease
{
    public const int SingletonId = 1;

    [Key]
    public int Id { get; set; } = SingletonId;

    public string OwnerInstanceId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public DateTime ExpiresAt { get; set; }
}
