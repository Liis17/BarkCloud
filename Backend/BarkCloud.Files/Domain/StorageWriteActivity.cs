namespace BarkCloud.Files.Domain;

/// <summary>An admitted HTTP upload or pipeline not represented by a resumable upload session.</summary>
public sealed class StorageWriteActivity
{
    public string Id { get; set; } = string.Empty;
    public string ProfileIdsJson { get; set; } = "[]";
    public Guid? FileId { get; set; }
}
