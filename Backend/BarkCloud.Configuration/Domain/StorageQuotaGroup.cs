namespace BarkCloud.Configuration.Domain;

public sealed class StorageQuotaGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long QuotaBytes { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime EditedAt { get; set; }

    public string EditedBy { get; set; } = string.Empty;

    public string EditedFrom { get; set; } = string.Empty;

    public List<StorageQuotaGroupBucket> Buckets { get; set; } = [];
}

public sealed class StorageQuotaGroupBucket
{
    public string GroupId { get; set; } = string.Empty;

    public string ServiceUrl { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;
}
