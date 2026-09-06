namespace BarkCloud.Files.Domain;

/// <summary>Личное имя файла, используемое только в поиске владельца.</summary>
public class FileSearchAlias
{
    public long OwnerId { get; set; }

    public Guid FileId { get; set; }

    public string Value { get; set; } = string.Empty;

    public string NormalizedValue { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
