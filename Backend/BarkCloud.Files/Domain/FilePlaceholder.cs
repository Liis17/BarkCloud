namespace BarkCloud.Files.Domain;

/// <summary>Девять цветов области превью, слева направо и сверху вниз.</summary>
public class FilePlaceholder
{
    public Guid FileId { get; set; }
    public Guid SourceFilePreviewId { get; set; }
    public string[] Colors { get; set; } = [];
    public float AspectRatio { get; set; }
}
