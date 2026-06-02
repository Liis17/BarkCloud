using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;

namespace BarkCloud.Files.Features.Cloud.GetSharedFileDownloadUrl;

public class GetSharedFileDownloadUrlCommandHandler : IRequestHandler<GetSharedFileDownloadUrlCommand, GetSharedFileDownloadUrlResponse>
{
    private readonly IGrantStorage _grantStorage;
    private readonly FolderGrantAccessService _folderAccess;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public GetSharedFileDownloadUrlCommandHandler(
        IGrantStorage grantStorage,
        FolderGrantAccessService folderAccess,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _grantStorage = grantStorage;
        _folderAccess = folderAccess;
        _filesStorage = filesStorage;
        _tempFilesStorage = tempFilesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<GetSharedFileDownloadUrlResponse> Handle(GetSharedFileDownloadUrlCommand request, CancellationToken cancellationToken)
    {
        var recipientId = _userContext.UserId;

        // Скачать может только получатель: либо прямой грант на файл, либо доступ через расшаренную папку.
        var hasAccess = await _grantStorage.RecipientHasAccess(recipientId, request.FileId, cancellationToken)
            || await _folderAccess.RecipientCanAccessFileViaFolder(recipientId, request.FileId, cancellationToken);
        if (!hasAccess)
            throw new CloudAccessDeniedException();

        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null)
            throw new FileNotFoundException();

        // Прямой /download/{fileId} для CloudFile запрещён — выдаём временную ссылку (как публичный резолв).
        var tempFiles = await _tempFilesStorage.CreateTempFilesBatchAsync(new[] { request.FileId }, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var downloadUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, tempFiles[0].Id);

        return new GetSharedFileDownloadUrlResponse { DownloadUrl = downloadUrl };
    }
}
