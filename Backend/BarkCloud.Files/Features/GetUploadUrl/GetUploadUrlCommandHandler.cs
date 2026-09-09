using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Infrastructure;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;


namespace BarkCloud.Files.Features.GetUploadUrl;

public class GetUploadUrlCommandHandler : IRequestHandler<GetUploadUrlCommand, GetUploadUrlResponse>
{

    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly UserContext _userContext;
    private readonly RequestContext _requestContext;
    private readonly ILogger<GetUploadUrlCommandHandler> _logger;
    private readonly S3BucketRegistry? _storageProfiles;


    public GetUploadUrlCommandHandler(IUploadedFilesStorage uploadedFilesStorage, UserContext userContext,
        RequestContext requestContext,
        RunSettings runSettings, IConfiguration configuration,
        ILogger<GetUploadUrlCommandHandler> logger,
        S3BucketRegistry? storageProfiles = null)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _userContext = userContext;
        _requestContext = requestContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
        _storageProfiles = storageProfiles;
    }

    public async Task<GetUploadUrlResponse> Handle(GetUploadUrlCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Запрос URL для загрузки файла. Тип: {FileType}, UserId: {UserId}, Device: {DeviceName}",
            request.Type,
            _userContext.UserId,
            _requestContext.DeviceName ?? "Unknown"
        );

        var uploadFile = new Domain.UploadFile()
        {
            CreatedAt = DateTime.UtcNow,
            Type = request.Type,
            Uploaders = new List<long> { _userContext.UserId },
            UploadDeviceName = _requestContext.DeviceName,
            StorageProfileId = _storageProfiles?.ResolveWriteProfileId(request.Type, Domain.MediaKind.Other, false)
                               ?? (request.Type == Domain.UploadFileType.UserAvatar
                                   ? S3BucketRegistry.UserAvatarsOldProfileId
                                   : S3BucketRegistry.CloudFilesOldProfileId),
        };

        var file = await _uploadedFilesStorage.AddToStorage(uploadFile);

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var uploadUrl = FileUrlHelper.GenerateUploadUrl(baseUrl, file.Id);

        _logger.LogInformation(
            "URL для загрузки создан. FileId: {FileId}, Тип: {FileType}, URL: {UploadUrl}",
            file.Id,
            request.Type,
            uploadUrl
        );

        return new GetUploadUrlResponse()
        {
            Url = uploadUrl,
            FileId = file.Id.ToString()
        };
    }
}
