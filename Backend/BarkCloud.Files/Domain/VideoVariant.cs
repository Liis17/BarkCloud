using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Связь оригинального видео с отдельным S3-блобом по целевой высоте кадра.
/// </summary>
public sealed class VideoVariant
{
    [Key]
    public Guid Id { get; set; }

    public Guid OriginalFileId { get; set; }

    public Guid VariantFileId { get; set; }

    public int Height { get; set; }

    public int Width { get; set; }

    public int ActualHeight { get; set; }

    public DateTime CreatedAt { get; set; }
}
