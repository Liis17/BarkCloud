namespace BarkCloud.Configuration.Domain;

public sealed class StorageProfileRevision
{
    public long Id { get; set; }

    public string ProfileId { get; set; } = string.Empty;

    public string PreviousValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;

    public DateTime ChangedAt { get; set; }

    public string ChangedBy { get; set; } = string.Empty;

    public string ChangedFrom { get; set; } = string.Empty;

    public string ChangeKind { get; set; } = string.Empty;

    public long? SourceRevisionId { get; set; }
}
