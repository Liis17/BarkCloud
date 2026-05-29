using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveShare;

public class ResolveShareCommandHandler : IRequestHandler<ResolveShareCommand, ResolveShareResponse>
{
    private readonly IShareStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResolveShareCommandHandler> _logger;

    public ResolveShareCommandHandler(
        IShareStorage storage,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ResolveShareCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _tempFilesStorage = tempFilesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ResolveShareResponse> Handle(ResolveShareCommand request, CancellationToken cancellationToken)
    {
        var share = await _storage.GetByToken(request.Token, cancellationToken);
        if (share is null)
            return new ResolveShareResponse { Found = false };

        // Файл мог быть удалён владельцем после создания ссылки — отдадим 404 вместо мёртвой temp-ссылки.
        var file = await _filesStorage.GetFile(share.FileId);
        if (file is null)
        {
            _logger.LogWarning("Резолв публичной ссылки {ShareId}: файл {FileId} не найден", share.Id, share.FileId);
            return new ResolveShareResponse { Found = false };
        }

        await _storage.IncrementClicks(share.Id, cancellationToken);

        // Прямой /download/{fileId} для CloudFile запрещён (DownloadFileCommandHandler) — нужна временная ссылка.
        var tempFiles = await _tempFilesStorage.CreateTempFilesBatchAsync(new[] { share.FileId }, cancellationToken);
        var tempId = tempFiles[0].Id;
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var downloadUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, tempId);

        _logger.LogInformation("Резолв публичной ссылки {ShareId} → файл {FileId} (temp {TempId})", share.Id, share.FileId, tempId);

        return new ResolveShareResponse
        {
            Found = true,
            FileId = share.FileId.ToString(),
            Name = share.Name,
            DownloadUrl = downloadUrl
        };
    }
}
