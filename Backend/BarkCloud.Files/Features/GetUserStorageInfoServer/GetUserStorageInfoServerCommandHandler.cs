using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetUserStorageInfoServer;

public class GetUserStorageInfoServerCommandHandler : IRequestHandler<GetUserStorageInfoServerCommand, GetUserStorageInfoResponse>
{
    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly IPhysicalStorageStatsProvider _storageStatsProvider;
    private readonly IS3StorageStatsProvider _s3StorageStatsProvider;
    private readonly IStorageQuotaService _quota;
    private readonly ILogger<GetUserStorageInfoServerCommandHandler> _logger;

    public GetUserStorageInfoServerCommandHandler(
        IUploadedFilesStorage uploadedFilesStorage,
        IPhysicalStorageStatsProvider storageStatsProvider,
        IS3StorageStatsProvider s3StorageStatsProvider,
        IStorageQuotaService quota,
        ILogger<GetUserStorageInfoServerCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _storageStatsProvider = storageStatsProvider;
        _s3StorageStatsProvider = s3StorageStatsProvider;
        _quota = quota;
        _logger = logger;
    }

    public async Task<GetUserStorageInfoResponse> Handle(GetUserStorageInfoServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Получение информации о хранилище. UserId: {UserId}", request.UserId);

        var storageStats = await _storageStatsProvider.GetStatsAsync(cancellationToken);
        var allS3Stats = await _s3StorageStatsProvider.GetStatsAsync(cancellationToken);
        var quota = await _quota.GetSnapshotAsync(
            request.UserId, acquireTransactionLock: false, cancellationToken);
        var storageByType = await _uploadedFilesStorage.GetUserStorageByType(request.UserId);

        var response = new GetUserStorageInfoResponse
        {
            TotalUsedStorage = quota.UsedBytes,
            StorageLimit = quota.LimitBytes ?? 0,
            ReservedStorage = quota.ReservedBytes,
            TotalAvailableStorage = storageStats.TotalBytes,
            DiskUsedStorage = storageStats.DiskUsedWithoutS3Bytes,
            S3UsedStorage = storageStats.S3UsedBytes,
            AllS3StatsAvailable = allS3Stats.IsAvailable,
            PhysicalStatsState = storageStats.State,
            PhysicalStatsUpdatedAt = storageStats.UpdatedAt is { } physicalAt
                ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(physicalAt) : null,
            AllS3StatsState = allS3Stats.State,
            AllS3StatsUpdatedAt = allS3Stats.UpdatedAt is { } s3At
                ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(s3At) : null
        };
        response.S3QuotaGroups.AddRange(allS3Stats.Groups.Select(group => new S3QuotaGroupUsage
        {
            Id = group.Id,
            Name = group.Name,
            UsedBytes = group.UsedBytes,
            QuotaBytes = group.QuotaBytes,
            IsUngrouped = group.IsUngrouped,
            UnmeasuredBuckets = group.UnmeasuredBuckets
        }));

        foreach (var (fileType, size) in storageByType)
        {
            response.StorageByTypes.Add(new GetUserStorageInfoResponse.Types.StorageByType
            {
                FileType = (UploadFileType)(int)fileType,
                UsedStorage = size
            });
        }

        return response;
    }
}
