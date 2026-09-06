namespace BarkCloud.Files.Domain;

/// <summary>Личный поисковый тег владельца файла.</summary>
public class FileTag
{
    public long OwnerId { get; set; }

    public Guid FileId { get; set; }

    public string Value { get; set; } = string.Empty;

    public string NormalizedValue { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
