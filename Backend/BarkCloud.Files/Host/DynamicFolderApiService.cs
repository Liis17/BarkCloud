using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.DynamicFolder.CreateDynamicFolder;
using BarkCloud.Files.Features.DynamicFolder.DeleteDynamicFolder;
using BarkCloud.Files.Features.DynamicFolder.ListDynamicFolderItems;
using BarkCloud.Files.Features.DynamicFolder.ListDynamicFolders;
using BarkCloud.Files.Features.DynamicFolder.UpdateDynamicFolder;
using BarkCloud.Files.Mapping;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Host;

/// <summary>
/// gRPC-сервис умных папок. Тонкий слой: маппит request в Command и шлёт через MediatR.
/// Системные папки (id вида "sys-*") нельзя изменять/удалять — отсекаем до парсинга Guid.
/// </summary>
[Authorize(Policy = nameof(TokenType.User))]
public class DynamicFolderApiService : DynamicFolderApi.DynamicFolderApiBase
{
    private readonly IMediator _mediator;

    public DynamicFolderApiService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override Task<DynamicFolderInfo> CreateDynamicFolder(CreateDynamicFolderRequest request, ServerCallContext context)
    {
        var command = new CreateDynamicFolderCommand
        {
            Name = request.Name,
            Criteria = DynamicFolderMapping.ToDomainCriteria(request.Combinator, request.Rules),
            IconKey = request.IconKey,
            CoverColor = request.CoverColor
        };

        return _mediator.Send(command);
    }

    public override Task<DynamicFolderInfo> UpdateDynamicFolder(UpdateDynamicFolderRequest request, ServerCallContext context)
    {
        if (SystemDynamicFolders.IsSystemKey(request.FolderId))
            throw new SystemDynamicFolderImmutableException();

        var command = new UpdateDynamicFolderCommand
        {
            FolderId = Guid.Parse(request.FolderId),
            Name = request.HasName ? request.Name : null,
            Criteria = DynamicFolderMapping.ToDomainCriteria(request.Combinator, request.Rules),
            IconKey = request.HasIconKey ? request.IconKey : null,
            CoverColor = request.HasCoverColor ? request.CoverColor : null
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> DeleteDynamicFolder(DeleteDynamicFolderRequest request, ServerCallContext context)
    {
        if (SystemDynamicFolders.IsSystemKey(request.FolderId))
            throw new SystemDynamicFolderImmutableException();

        var command = new DeleteDynamicFolderCommand
        {
            FolderId = Guid.Parse(request.FolderId)
        };

        return _mediator.Send(command);
    }

    public override Task<ListDynamicFoldersResponse> ListDynamicFolders(ListDynamicFoldersRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ListDynamicFoldersCommand());
    }

    public override Task<ListDynamicFolderItemsResponse> ListDynamicFolderItems(ListDynamicFolderItemsRequest request, ServerCallContext context)
    {
        DateTime? cursorCreatedAt = null;
        Guid? cursorFileId = null;
        if (request.CursorCreatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorCreatedAt = request.CursorCreatedAt.ToDateTime();
            cursorFileId = Guid.Parse(request.CursorFileId);
        }

        var command = new ListDynamicFolderItemsCommand
        {
            FolderId = request.FolderId,
            Limit = request.Limit,
            CursorCreatedAt = cursorCreatedAt,
            CursorFileId = cursorFileId,
            KindFilter = request.HasKindFilter ? (DomainMediaKind)(int)request.KindFilter : null
        };

        return _mediator.Send(command);
    }
}
