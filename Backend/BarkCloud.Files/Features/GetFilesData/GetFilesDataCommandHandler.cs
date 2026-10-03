using BarkCloud.Files.Helpers;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using MediatR;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.GetFilesData;

public class GetFilesDataCommandHandler : IRequestHandler<GetFilesDataCommand, GetFilesDataResponse>
{
    private readonly IUploadedFilesStorage _uploadedFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GetFilesDataCommandHandler> _logger;

    public GetFilesDataCommandHandler(IUploadedFilesStorage uploadedFilesStorage, RunSettings runSettings,
        IConfiguration configuration, ILogger<GetFilesDataCommandHandler> logger)
    {
        _uploadedFilesStorage = uploadedFilesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GetFilesDataResponse> Handle(GetFilesDataCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Запрос данных для {FileCount} файлов",
            request.FileIds.Count()
        );

        var files = (await _uploadedFilesStorage.GetFiles(request.FileIds))
            .Where(x => x.IsReady())
            .ToList();

        // Аватары публичны; остальное — только владельцу. Любой недоступный файл — отказ на весь запрос.
        var foreignCount = files.Count(f => f.Type != UploadFileType.UserAvatar && !f.Uploaders.Contains(request.UserId));
        if (foreignCount > 0)
        {
            _logger.LogWarning(
                "Пользователь {UserId} запросил данные {ForeignCount} недоступных файлов из {RequestedCount}",
                request.UserId,
                foreignCount,
                request.FileIds.Count()
            );
            throw new BarkCloud.Shared.Exceptions.Files.CloudAccessDeniedException();
        }

        _logger.LogInformation(
            "Получены данные для {FoundCount} файлов из {RequestedCount} запрошенных",
            files.Count(),
            request.FileIds.Count()
        );

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        var previewsByOriginal = await _uploadedFilesStorage.GetPreviewsForFiles(
            files.Select(f => f.Id), cancellationToken);
        var placeholders = await _uploadedFilesStorage.GetPlaceholdersForFiles(files.Select(f => f.Id), cancellationToken);

        return new GetFilesDataResponse
        {
            FilesInfos =
            {
                files.Select(x => x.ToGrpc(
                    baseUrl,
                    previewsByOriginal.TryGetValue(x.Id, out var ps) ? ps : null,
                    placeholder: placeholders.GetValueOrDefault(x.Id)))
            }
        };
    }
}
