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
    private readonly IStorageQuotaService _quota;
    private readonly UserContext _userContext;
    private readonly ILogger<GetUserStorageInfoCommandHandler> _logger;

    public GetUserStorageInfoCommandHandler(
        IUploadedFilesStorage uploadedFilesStorage,
        IPhysicalStorageStatsProvider storageStatsProvider,
        IStorageQuotaService quota,
        UserContext userContext,
        ILogger<GetUserStorageInfoCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _storageStatsProvider = storageStatsProvider;
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
            S3UsedStorage = storageStats.S3UsedBytes
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
