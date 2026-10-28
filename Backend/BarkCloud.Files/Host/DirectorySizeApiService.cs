using BarkCloud.Files.Features.Cloud.GetSubdirectorySizes;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Files.Host;

/// <summary>
/// gRPC-сервис размеров подпапок. Тонкий слой: собирает команду и отправляет её через MediatR.
/// </summary>
[Authorize(Policy = nameof(TokenType.User))]
public class DirectorySizeApiService : DirectorySizeApi.DirectorySizeApiBase
{
    private readonly IMediator _mediator;

    public DirectorySizeApiService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override Task<GetSubdirectorySizesResponse> GetSubdirectorySizes(GetSubdirectorySizesRequest request, ServerCallContext context) =>
        _mediator.Send(new GetSubdirectorySizesCommand
        {
            ParentId = string.IsNullOrEmpty(request.ParentId) ? null : Guid.Parse(request.ParentId)
        });
}
