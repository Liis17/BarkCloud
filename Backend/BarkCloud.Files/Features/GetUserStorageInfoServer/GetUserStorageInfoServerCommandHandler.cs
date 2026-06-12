using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetUserStorageInfoServer;

public class GetUserStorageInfoServerCommandHandler : IRequestHandler<GetUserStorageInfoServerCommand, GetUserStorageInfoResponse>
{
    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly IPhysicalStorageStatsProvider _storageStatsProvider;
    private readonly BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient _usersClient;
    private readonly ILogger<GetUserStorageInfoServerCommandHandler> _logger;

    public GetUserStorageInfoServerCommandHandler(
        IUploadedFilesStorage uploadedFilesStorage,
        IPhysicalStorageStatsProvider storageStatsProvider,
        BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient usersClient,
        ILogger<GetUserStorageInfoServerCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _storageStatsProvider = storageStatsProvider;
        _usersClient = usersClient;
        _logger = logger;
    }

    public async Task<GetUserStorageInfoResponse> Handle(GetUserStorageInfoServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Получение информации о хранилище. UserId: {UserId}", request.UserId);

        var userResponse = await _usersClient.GetByIdAsync(new BarkCloud.Proto.Users.GetByIdRequest
        {
            UserId = request.UserId
        }, cancellationToken: cancellationToken);

        var storageStats = await _storageStatsProvider.GetStatsAsync(cancellationToken);
        var storageLimitBytes = userResponse.User.StorageLimitGb > 0
            ? (long)userResponse.User.StorageLimitGb * 1024 * 1024 * 1024
            : storageStats.TotalBytes;

        var totalUsedStorage = await _uploadedFilesStorage.GetUserStorageUsed(request.UserId);
        var storageByType = await _uploadedFilesStorage.GetUserStorageByType(request.UserId);

        var response = new GetUserStorageInfoResponse
        {
            TotalUsedStorage = totalUsedStorage,
            StorageLimit = storageLimitBytes,
            TotalAvailableStorage = storageStats.TotalBytes,
            DiskUsedStorage = storageStats.DiskUsedWithoutS3Bytes,
            S3UsedStorage = storageStats.S3UsedBytes
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
