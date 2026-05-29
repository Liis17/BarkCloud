using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveShare;

public class ResolveShareCommandHandler : IRequestHandler<ResolveShareCommand, ResolveShareResponse>
{
    private readonly ShareStorage _storage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResolveShareCommandHandler> _logger;

    public ResolveShareCommandHandler(
        ShareStorage storage,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ResolveShareCommandHandler> logger)
    {
        _storage = storage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ResolveShareResponse> Handle(ResolveShareCommand request, CancellationToken cancellationToken)
    {
        var share = await _storage.GetByToken(request.Token, cancellationToken);
        if (share is null)
            return new ResolveShareResponse { Found = false };

        await _storage.IncrementClicks(share.Id, cancellationToken);

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var downloadUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, share.FileId);

        _logger.LogInformation("Резолв публичной ссылки {ShareId} → файл {FileId}", share.Id, share.FileId);

        return new ResolveShareResponse
        {
            Found = true,
            FileId = share.FileId.ToString(),
            Name = share.Name,
            DownloadUrl = downloadUrl
        };
    }
}
