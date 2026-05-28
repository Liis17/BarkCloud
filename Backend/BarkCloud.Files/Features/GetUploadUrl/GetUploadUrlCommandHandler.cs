using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
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


    public GetUploadUrlCommandHandler(IUploadedFilesStorage uploadedFilesStorage, UserContext userContext,
        RequestContext requestContext,
        RunSettings runSettings, IConfiguration configuration,
        ILogger<GetUploadUrlCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _userContext = userContext;
        _requestContext = requestContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
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