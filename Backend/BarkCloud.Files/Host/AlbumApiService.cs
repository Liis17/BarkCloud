using BarkCloud.Files.Features.Album.AddItemsToAlbum;
using BarkCloud.Files.Features.Album.CreateAlbum;
using BarkCloud.Files.Features.Album.DeleteAlbum;
using BarkCloud.Files.Features.Album.ListAlbumItems;
using BarkCloud.Files.Features.Album.ListAlbums;
using BarkCloud.Files.Features.Album.RemoveItemsFromAlbum;
using BarkCloud.Files.Features.Album.UpdateAlbum;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Host;

/// <summary>
/// gRPC-сервис для работы с альбомами пользователя. Тонкий слой: каждый метод
/// оборачивает аргументы в Command и шлёт через MediatR.
/// </summary>
[Authorize(Policy = nameof(TokenType.User))]
public class AlbumApiService : AlbumApi.AlbumApiBase
{
    private readonly IMediator _mediator;

    public AlbumApiService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override Task<AlbumInfo> CreateAlbum(CreateAlbumRequest request, ServerCallContext context)
    {
        var command = new CreateAlbumCommand
        {
            Name = request.Name,
            Description = request.Description
        };

        return _mediator.Send(command);
    }

    public override Task<AlbumInfo> UpdateAlbum(UpdateAlbumRequest request, ServerCallContext context)
    {
        var command = new UpdateAlbumCommand
        {
            AlbumId = Guid.Parse(request.AlbumId),
            Name = request.HasName ? request.Name : null,
            Description = request.HasDescription ? request.Description : null,
            UpdateCover = request.HasCoverFileId,
            CoverFileId = request.HasCoverFileId && !string.IsNullOrWhiteSpace(request.CoverFileId)
                ? Guid.Parse(request.CoverFileId)
                : null
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> DeleteAlbum(DeleteAlbumRequest request, ServerCallContext context)
    {
        var command = new DeleteAlbumCommand
        {
            AlbumId = Guid.Parse(request.AlbumId)
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> AddItemsToAlbum(AddItemsToAlbumRequest request, ServerCallContext context)
    {
        var command = new AddItemsToAlbumCommand
        {
            AlbumId = Guid.Parse(request.AlbumId),
            FileIds = request.FileIds.Select(Guid.Parse).ToList()
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> RemoveItemsFromAlbum(RemoveItemsFromAlbumRequest request, ServerCallContext context)
    {
        var command = new RemoveItemsFromAlbumCommand
        {
            AlbumId = Guid.Parse(request.AlbumId),
            FileIds = request.FileIds.Select(Guid.Parse).ToList()
        };

        return _mediator.Send(command);
    }

    public override Task<ListAlbumsResponse> ListAlbums(ListAlbumsRequest request, ServerCallContext context)
    {
        DateTime? cursorUpdatedAt = null;
        Guid? cursorAlbumId = null;
        if (request.CursorUpdatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorAlbumId))
        {
            cursorUpdatedAt = request.CursorUpdatedAt.ToDateTime();
            cursorAlbumId = Guid.Parse(request.CursorAlbumId);
        }

        var command = new ListAlbumsCommand
        {
            Limit = request.Limit,
            CursorUpdatedAt = cursorUpdatedAt,
            CursorAlbumId = cursorAlbumId
        };

        return _mediator.Send(command);
    }

    public override Task<ListAlbumItemsResponse> ListAlbumItems(ListAlbumItemsRequest request, ServerCallContext context)
    {
        DateTime? cursorAddedAt = null;
        Guid? cursorFileId = null;
        if (request.CursorAddedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorAddedAt = request.CursorAddedAt.ToDateTime();
            cursorFileId = Guid.Parse(request.CursorFileId);
        }

        var command = new ListAlbumItemsCommand
        {
            AlbumId = Guid.Parse(request.AlbumId),
            Limit = request.Limit,
            CursorAddedAt = cursorAddedAt,
            CursorFileId = cursorFileId,
            KindFilter = request.HasKindFilter ? (DomainMediaKind)(int)request.KindFilter : null
        };

        return _mediator.Send(command);
    }
}
