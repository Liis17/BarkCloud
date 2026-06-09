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
    private readonly UserContext _userContext;
    private readonly BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient _usersClient;
    private readonly ILogger<GetUserStorageInfoCommandHandler> _logger;

    public GetUserStorageInfoCommandHandler(
        IUploadedFilesStorage uploadedFilesStorage,
        IPhysicalStorageStatsProvider storageStatsProvider,
        UserContext userContext,
        BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient usersClient,
        ILogger<GetUserStorageInfoCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _storageStatsProvider = storageStatsProvider;
        _userContext = userContext;
        _usersClient = usersClient;
        _logger = logger;
    }

    public async Task<GetUserStorageInfoResponse> Handle(GetUserStorageInfoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Запрос информации о хранилище. UserId: {UserId}",
            _userContext.UserId
        );

        // Получаем информацию о пользователе для получения лимита
        var userResponse = await _usersClient.GetByIdAsync(new BarkCloud.Proto.Users.GetByIdRequest
        {
            UserId = _userContext.UserId
        }, cancellationToken: cancellationToken);

        var storageStats = await _storageStatsProvider.GetStatsAsync(cancellationToken);

        var storageLimitBytes = userResponse.User.StorageLimitGb > 0
            ? (long)userResponse.User.StorageLimitGb * 1024 * 1024 * 1024
            : storageStats.TotalBytes;

        // Получаем общее использованное пространство
        var totalUsedStorage = await _uploadedFilesStorage.GetUserStorageUsed(_userContext.UserId);

        // Получаем использованное пространство по типам файлов
        var storageByType = await _uploadedFilesStorage.GetUserStorageByType(_userContext.UserId);

        var response = new GetUserStorageInfoResponse
        {
            TotalUsedStorage = totalUsedStorage,
            StorageLimit = storageLimitBytes,
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
            totalUsedStorage,
            storageLimitBytes,
            storageStats.S3UsedBytes
        );

        return response;
    }
}
