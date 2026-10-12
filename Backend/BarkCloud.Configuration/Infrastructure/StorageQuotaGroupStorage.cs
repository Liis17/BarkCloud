using BarkCloud.Configuration.Domain;
using BarkCloud.GrpcServer;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Infrastructure;

public sealed record StorageQuotaGroupBucketInput(string ServiceUrl, string BucketName);

public sealed class StorageQuotaGroupStorage
{
    private readonly ConfigurationContext _context;

    public StorageQuotaGroupStorage(ConfigurationContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<StorageQuotaGroup>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _context.StorageQuotaGroups.AsNoTracking()
            .Include(group => group.Buckets)
            .OrderBy(group => group.Name)
            .ToListAsync(cancellationToken);

    public async Task<StorageQuotaGroup> SaveAsync(
        string? id,
        string name,
        long quotaBytes,
        IReadOnlyCollection<StorageQuotaGroupBucketInput> buckets,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new InvalidOperationException("Укажите название группы.");
        if (name.Length > 100)
            throw new InvalidOperationException("Название группы не должно превышать 100 символов.");
        if (quotaBytes < 0)
            throw new InvalidOperationException("Квота группы не может быть отрицательной.");

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var profiles = await GetProfilesForUpdateAsync(cancellationToken);
        var groups = await _context.StorageQuotaGroups
            .Include(group => group.Buckets)
            .ToListAsync(cancellationToken);
        var group = string.IsNullOrWhiteSpace(id)
            ? null
            : groups.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("Группа S3 не найдена.");

        if (groups.Any(item => item.Id != group?.Id && item.Name == name))
            throw new InvalidOperationException("Группа с таким названием уже существует.");

        var savedBuckets = new List<(string ServiceUrl, string BucketName)>();
        foreach (var input in buckets)
        {
            var bucketName = input.BucketName?.Trim() ?? string.Empty;
            if (bucketName.Length == 0)
                throw new InvalidOperationException("Укажите имя бакета.");

            var profile = profiles.FirstOrDefault(item => item.BucketName == bucketName
                && ProfileMatchesEndpoint(item, input.ServiceUrl));
            string serviceUrl;
            if (profile is not null)
            {
                serviceUrl = S3Endpoint.Normalize(profile.ServiceUrl, profile.IsR2);
            }
            else
            {
                var previouslySaved = group?.Buckets.FirstOrDefault(item => item.BucketName == bucketName
                    && SameEndpoint(item.ServiceUrl, input.ServiceUrl));
                if (previouslySaved is null)
                    throw new InvalidOperationException($"Бакет {bucketName} отсутствует в S3-профилях.");
                serviceUrl = previouslySaved.ServiceUrl;
            }

            if (savedBuckets.Any(item => SameLocation(item.ServiceUrl, item.BucketName, serviceUrl, bucketName)))
                throw new InvalidOperationException($"Бакет {bucketName} указан несколько раз.");

            var existingGroup = groups.FirstOrDefault(item => item.Id != group?.Id
                && item.Buckets.Any(existing => SameLocation(
                    existing.ServiceUrl, existing.BucketName, serviceUrl, bucketName)));
            if (existingGroup is not null)
                throw new InvalidOperationException($"Бакет {bucketName} уже входит в группу {existingGroup.Name}.");

            savedBuckets.Add((serviceUrl, bucketName));
        }

        var actor = string.IsNullOrWhiteSpace(editedBy) ? "unknown" : editedBy.Trim();
        var source = editedFrom?.Trim() ?? string.Empty;
        var now = DateTime.UtcNow;
        if (group is null)
        {
            group = new StorageQuotaGroup
            {
                Id = $"grp-{Guid.NewGuid():N}",
                CreatedAt = now,
                CreatedBy = actor
            };
            _context.StorageQuotaGroups.Add(group);
        }

        group.Name = name;
        group.QuotaBytes = quotaBytes;
        group.EditedAt = now;
        group.EditedBy = actor;
        group.EditedFrom = source;

        var removedBuckets = group.Buckets.Where(existing => !savedBuckets.Any(item =>
            SameLocation(existing.ServiceUrl, existing.BucketName, item.ServiceUrl, item.BucketName))).ToArray();
        _context.StorageQuotaGroupBuckets.RemoveRange(removedBuckets);
        foreach (var bucket in savedBuckets.Where(item => !group.Buckets.Any(existing =>
                     SameLocation(existing.ServiceUrl, existing.BucketName, item.ServiceUrl, item.BucketName))))
        {
            group.Buckets.Add(new StorageQuotaGroupBucket
            {
                GroupId = group.Id,
                ServiceUrl = bucket.ServiceUrl,
                BucketName = bucket.BucketName
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var group = await _context.StorageQuotaGroups.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Группа S3 не найдена.");
        _context.StorageQuotaGroups.Remove(group);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<List<StorageProfile>> GetProfilesForUpdateAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return await _context.StorageProfiles
                .FromSqlRaw("SELECT * FROM \"StorageProfiles\" FOR UPDATE")
                .ToListAsync(cancellationToken);
        }

        return await _context.StorageProfiles.ToListAsync(cancellationToken);
    }

    private static bool ProfileMatchesEndpoint(StorageProfile profile, string serviceUrl)
    {
        try
        {
            return string.Equals(S3Endpoint.Normalize(profile.ServiceUrl, profile.IsR2),
                S3Endpoint.Normalize(serviceUrl, profile.IsR2), StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException("Укажите корректный endpoint бакета.", exception);
        }
    }

    private static bool SameEndpoint(string left, string right)
    {
        try
        {
            return string.Equals(S3Endpoint.Normalize(left), S3Endpoint.Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException("Укажите корректный endpoint бакета.", exception);
        }
    }

    private static bool SameLocation(string leftUrl, string leftBucket, string rightUrl, string rightBucket) =>
        string.Equals(leftUrl, rightUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(leftBucket, rightBucket, StringComparison.Ordinal);
}
