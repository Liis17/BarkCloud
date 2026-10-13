namespace BarkCloud.Files.Configurations;

public sealed class StorageQuotaGroupOptions
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long QuotaBytes { get; set; }

    public List<StorageQuotaGroupBucketOptions> Buckets { get; set; } = [];
}

public sealed class StorageQuotaGroupBucketOptions
{
    public string ServiceUrl { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;
}
