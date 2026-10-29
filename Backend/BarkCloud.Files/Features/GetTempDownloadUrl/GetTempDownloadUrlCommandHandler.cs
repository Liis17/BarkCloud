using BarkCloud.Files.Helpers;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetTempDownloadUrl;

public class GetTempDownloadUrlCommandHandler : IRequestHandler<GetTempDownloadUrlCommand, GetTempDownloadUrlResponse>
{
    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GetTempDownloadUrlCommandHandler> _logger;

    public GetTempDownloadUrlCommandHandler(IUploadedFilesStorage uploadedFilesStorage, ITempFilesStorage tempFilesStorage,
        UserContext userContext, RunSettings runSettings, IConfiguration configuration,
        ILogger<GetTempDownloadUrlCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _tempFilesStorage = tempFilesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GetTempDownloadUrlResponse> Handle(GetTempDownloadUrlCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Запрос временных URL для скачивания {FileCount} файлов",
            request.FileIds.Count()
        );

        var storedFiles = await _uploadedFilesStorage.GetFiles(request.FileIds);
        if (storedFiles is null)
        {
            throw new FileNotFoundException();
        }
        var files = storedFiles.Where(x => x.IsReady()).ToList();

        // Ссылку на оригинал получает только владелец. Чужой файл в запросе — отказ на весь запрос,
        // ни одного TempFile не создаётся. Получатели шаринга качают через GetSharedFileDownloadUrl.
        var userId = _userContext.UserId;
        var foreignCount = files.Count(f => !f.Uploaders.Contains(userId));
        if (foreignCount > 0)
        {
            _logger.LogWarning(
                "Пользователь {UserId} запросил ссылки на {ForeignCount} чужих файлов из {RequestedCount}",
                userId,
                foreignCount,
                request.FileIds.Count
            );
            throw new BarkCloud.Shared.Exceptions.Files.CloudAccessDeniedException();
        }

        if (files.Count != request.FileIds.Count)
        {
            _logger.LogWarning(
                "Найдено {FoundCount} файлов из {RequestedCount} запрошенных",
                files.Count,
                request.FileIds.Count()
            );
        }

        var response = new GetTempDownloadUrlResponse()
        {
            FileUrls = { }
        };

        _logger.LogDebug("Создание временных ссылок для {FileCount} файлов", files.Count);

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        var downloadLinks = await VideoQualityLinkBuilder.CreateAsync(
            _uploadedFilesStorage, _tempFilesStorage, files, baseUrl, cancellationToken);

        foreach (var file in files)
        {
            if (!downloadLinks.TryGetValue(file.Id, out var links) || string.IsNullOrEmpty(links.OriginalUrl))
                continue;

            _logger.LogDebug("Создана временная ссылка для файла {FileId}", file.Id);

            var fileData = new GetTempDownloadUrlResponse.Types.DownloadFileData
            {
                FileId = file.Id.ToString(),
                Url = links.OriginalUrl
            };
            fileData.VideoQualities.AddRange(links.VideoQualities);
            response.FileUrls.Add(fileData);
        }

        _logger.LogInformation(
            "Создано {UrlCount} временных URL для скачивания файлов",
            response.FileUrls.Count
        );

        return response;
    }
}
