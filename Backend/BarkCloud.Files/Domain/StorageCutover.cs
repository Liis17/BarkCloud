namespace BarkCloud.Files.Domain;

/// <summary>Durable write barrier, independent of the Web copy job and its credentials.</summary>
public sealed class StorageCutover
{
    public string MigrationId { get; set; } = string.Empty;
    public string State { get; set; } = "draining";
    public string SourceServiceUrl { get; set; } = string.Empty;
    public string SourceBucketName { get; set; } = string.Empty;
    public string ProfileIdsJson { get; set; } = "[]";
    public string TargetServiceUrl { get; set; } = string.Empty;
    public string TargetBucketName { get; set; } = string.Empty;
    public string TargetRegion { get; set; } = string.Empty;
    public bool TargetForcePathStyle { get; set; }
    public bool TargetIsR2 { get; set; }
    public string TargetConnectionHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
