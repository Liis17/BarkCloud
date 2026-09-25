using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetUserStorageInfo;

public class GetUserStorageInfoCommandHandler : IRequestHandler<GetUserStorageInfoCommand, GetUserStorageInfoResponse>
{
    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly IPhysicalStorageStatsProvider _storageStatsProvider;
    private readonly IS3StorageStatsProvider _s3StorageStatsProvider;
    private readonly IStorageQuotaService _quota;
    private readonly UserContext _userContext;
    private readonly ILogger<GetUserStorageInfoCommandHandler> _logger;

    public GetUserStorageInfoCommandHandler(
        IUploadedFilesStorage uploadedFilesStorage,
        IPhysicalStorageStatsProvider storageStatsProvider,
        IS3StorageStatsProvider s3StorageStatsProvider,
        IStorageQuotaService quota,
        UserContext userContext,
        ILogger<GetUserStorageInfoCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _storageStatsProvider = storageStatsProvider;
        _s3StorageStatsProvider = s3StorageStatsProvider;
        _quota = quota;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<GetUserStorageInfoResponse> Handle(GetUserStorageInfoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Запрос информации о хранилище. UserId: {UserId}",
            _userContext.UserId
        );

        var storageStats = await _storageStatsProvider.GetStatsAsync(cancellationToken);
        var allS3Stats = await _s3StorageStatsProvider.GetStatsAsync(cancellationToken);
        var quota = await _quota.GetSnapshotAsync(
            _userContext.UserId, acquireTransactionLock: false, cancellationToken);

        // Получаем использованное пространство по типам файлов
        var storageByType = await _uploadedFilesStorage.GetUserStorageByType(_userContext.UserId);

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

        // Добавляем информацию по типам файлов
        foreach (var (fileType, size) in storageByType)
        {
            response.StorageByTypes.Add(new GetUserStorageInfoResponse.Types.StorageByType
            {
                FileType = (Proto.Files.UploadFileType)(int)fileType,
                UsedStorage = size
            });
        }

        _logger.LogInformation(
            "Информация о хранилище получена. UserId: {UserId}, Использовано: {UsedStorage} байт, Лимит: {TotalStorage} байт, S3: {S3UsedStorage} байт",
            _userContext.UserId,
            quota.UsedBytes,
            quota.LimitBytes ?? 0,
            storageStats.S3UsedBytes
        );

        return response;
    }
}
