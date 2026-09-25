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
            AllS3UsedStorage = allS3Stats.UsedBytes,
            AllS3QuotaStorage = allS3Stats.QuotaBytes,
            AllS3HasFiniteQuota = allS3Stats.HasFiniteQuota,
            AllS3StatsAvailable = allS3Stats.IsAvailable
        };

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
