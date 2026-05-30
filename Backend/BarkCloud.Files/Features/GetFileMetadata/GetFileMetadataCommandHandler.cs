using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetFileMetadata;

public class GetFileMetadataCommandHandler : IRequestHandler<GetFileMetadataCommand, GetFileMetadataResponse>
{
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly IFileMetadataStorage _metadataStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<GetFileMetadataCommandHandler> _logger;

    public GetFileMetadataCommandHandler(
        IUploadedFilesStorage filesStorage,
        IFileMetadataStorage metadataStorage,
        UserContext userContext,
        ILogger<GetFileMetadataCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _metadataStorage = metadataStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<GetFileMetadataResponse> Handle(GetFileMetadataCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Доступ только к собственным файлам (по правилу: пользователь должен быть в Uploaders).
        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null)
            throw new BarkCloud.Shared.Exceptions.Files.FileNotFoundException();
        if (!file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        var metadata = await _metadataStorage.Get(request.FileId, cancellationToken);
        if (metadata is null)
            return new GetFileMetadataResponse { HasMetadata = false };

        return new GetFileMetadataResponse
        {
            HasMetadata = true,
            Metadata = metadata.ToGrpc()
        };
    }
}
