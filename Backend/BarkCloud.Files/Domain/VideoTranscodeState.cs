using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

public enum VideoTranscodeStatus
{
    Pending = 0,
    Done = 1,
    Skipped = 2,
    Failed = 3,
}

/// <summary>
/// Состояние генерации вариантов одного исходного видео.
/// </summary>
public sealed class VideoTranscodeState
{
    [Key]
    public Guid FileId { get; set; }

    public VideoTranscodeStatus Status { get; set; }

    public int Attempts { get; set; }

    public List<int> SkippedHeights { get; set; } = [];

    public string? LastError { get; set; }

    public Guid? PendingVariantFileId { get; set; }

    public string? PendingProfileId { get; set; }

    public DateTime UpdatedAt { get; set; }
}
