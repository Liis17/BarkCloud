using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Features.Cloud.CreateDirectory;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.DeleteFileEntry;
using BarkCloud.Files.Features.Cloud.DeleteFromTrash;
using BarkCloud.Files.Features.Cloud.EmptyTrash;
using BarkCloud.Files.Features.Cloud.GetPath;
using BarkCloud.Files.Features.Cloud.ListTrash;
using BarkCloud.Files.Features.Cloud.ListDirectory;
using BarkCloud.Files.Features.Cloud.ListDirectoryDetailed;
using BarkCloud.Files.Features.Cloud.ListUserImages;
using BarkCloud.Files.Features.Cloud.ListUserMedia;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
using BarkCloud.Files.Features.Cloud.MoveFileEntry;
using BarkCloud.Files.Features.Cloud.RenameDirectory;
using BarkCloud.Files.Features.Cloud.RenameFileEntry;
using BarkCloud.Files.Features.Cloud.RestoreFromTrash;
using BarkCloud.Files.Features.Cloud.SetVideoThumbnail;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;

namespace BarkCloud.Files.Host;

/// <summary>
/// gRPC-сервис для работы с NextCloud-подобной иерархией папок и файловых записей.
/// Тонкий слой: каждый метод оборачивает аргументы в Command и шлёт через MediatR.
/// </summary>
[Authorize(Policy = nameof(TokenType.User))]
public class CloudApiService : CloudApi.CloudApiBase
{
    private readonly IMediator _mediator;

    public CloudApiService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override Task<DirectoryInfo> CreateDirectory(CreateDirectoryRequest request, ServerCallContext context)
    {
        var command = new CreateDirectoryCommand
        {
            ParentId = ParseOptionalGuid(request.ParentId),
            Name = request.Name
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> RenameDirectory(RenameDirectoryRequest request, ServerCallContext context)
    {
        var command = new RenameDirectoryCommand
        {
            DirectoryId = Guid.Parse(request.DirectoryId),
            NewName = request.NewName
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> MoveDirectory(MoveDirectoryRequest request, ServerCallContext context)
    {
        var command = new MoveDirectoryCommand
        {
            DirectoryId = Guid.Parse(request.DirectoryId),
            NewParentId = ParseOptionalGuid(request.NewParentId)
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> DeleteDirectory(DeleteDirectoryRequest request, ServerCallContext context)
    {
        var command = new DeleteDirectoryCommand
        {
            DirectoryId = Guid.Parse(request.DirectoryId)
        };

        return _mediator.Send(command);
    }

    public override Task<DirectoryListing> ListDirectory(ListDirectoryRequest request, ServerCallContext context)
    {
        var command = new ListDirectoryCommand
        {
            DirectoryId = request.HasDirectoryId ? ParseOptionalGuid(request.DirectoryId) : null
        };

        return _mediator.Send(command);
    }

    public override Task<DirectoryListingDetailed> ListDirectoryDetailed(ListDirectoryRequest request, ServerCallContext context)
    {
        var command = new ListDirectoryDetailedCommand
        {
            DirectoryId = request.HasDirectoryId ? ParseOptionalGuid(request.DirectoryId) : null
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> AttachFile(AttachFileRequest request, ServerCallContext context)
    {
        var command = new AttachFileCommand
        {
            DirectoryId = ParseOptionalGuid(request.DirectoryId),
            FileId = Guid.Parse(request.FileId),
            Name = request.Name
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> RenameFileEntry(RenameFileEntryRequest request, ServerCallContext context)
    {
        var command = new RenameFileEntryCommand
        {
            EntryId = Guid.Parse(request.EntryId),
            NewName = request.NewName
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> MoveFileEntry(MoveFileEntryRequest request, ServerCallContext context)
    {
        var command = new MoveFileEntryCommand
        {
            EntryId = Guid.Parse(request.EntryId),
            NewDirectoryId = ParseOptionalGuid(request.NewDirectoryId)
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> DeleteFileEntry(DeleteFileEntryRequest request, ServerCallContext context)
    {
        var command = new DeleteFileEntryCommand
        {
            EntryId = Guid.Parse(request.EntryId)
        };

        return _mediator.Send(command);
    }

    public override Task<ListUserImagesResponse> ListUserImages(ListUserImagesRequest request, ServerCallContext context)
    {
        DateTime? cursorCreatedAt = null;
        Guid? cursorFileId = null;
        if (request.CursorCreatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorCreatedAt = request.CursorCreatedAt.ToDateTime();
            cursorFileId = Guid.Parse(request.CursorFileId);
        }

        var command = new ListUserImagesCommand
        {
            Limit = request.Limit,
            CursorCreatedAt = cursorCreatedAt,
            CursorFileId = cursorFileId
        };

        return _mediator.Send(command);
    }

    public override Task<ListUserMediaResponse> ListUserMedia(ListUserMediaRequest request, ServerCallContext context)
    {
        DateTime? cursorCreatedAt = null;
        Guid? cursorFileId = null;
        if (request.CursorCreatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorCreatedAt = request.CursorCreatedAt.ToDateTime();
            cursorFileId = Guid.Parse(request.CursorFileId);
        }

        var command = new ListUserMediaCommand
        {
            Kind = (BarkCloud.Files.Domain.MediaKind)(int)request.Kind,
            Limit = request.Limit,
            CursorCreatedAt = cursorCreatedAt,
            CursorFileId = cursorFileId
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> SetVideoThumbnail(SetVideoThumbnailRequest request, ServerCallContext context)
    {
        var command = new SetVideoThumbnailCommand
        {
            VideoFileId = Guid.Parse(request.VideoFileId),
            SourceImageFileId = Guid.Parse(request.SourceImageFileId)
        };

        return _mediator.Send(command);
    }

    public override Task<ListTrashResponse> ListTrash(ListTrashRequest request, ServerCallContext context)
    {
        DateTime? cursorDeletedAt = null;
        Guid? cursorEntryId = null;
        if (request.CursorDeletedAt is not null && !string.IsNullOrWhiteSpace(request.CursorEntryId))
        {
            cursorDeletedAt = request.CursorDeletedAt.ToDateTime();
            cursorEntryId = Guid.Parse(request.CursorEntryId);
        }

        var command = new ListTrashCommand
        {
            Limit = request.Limit,
            CursorDeletedAt = cursorDeletedAt,
            CursorEntryId = cursorEntryId
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> RestoreFromTrash(RestoreFromTrashRequest request, ServerCallContext context)
    {
        var command = new RestoreFromTrashCommand
        {
            EntryId = Guid.Parse(request.EntryId)
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> DeleteFromTrash(DeleteFromTrashRequest request, ServerCallContext context)
    {
        var command = new DeleteFromTrashCommand
        {
            EntryId = Guid.Parse(request.EntryId)
        };

        return _mediator.Send(command);
    }

    public override Task<CloudEmpty> EmptyTrash(EmptyTrashRequest request, ServerCallContext context)
    {
        return _mediator.Send(new EmptyTrashCommand());
    }

    public override Task<PathResponse> GetPath(GetPathRequest request, ServerCallContext context)
    {
        var command = new GetPathCommand();
        switch (request.TargetCase)
        {
            case GetPathRequest.TargetOneofCase.DirectoryId:
                command.DirectoryId = ParseOptionalGuid(request.DirectoryId);
                break;
            case GetPathRequest.TargetOneofCase.EntryId:
                command.EntryId = ParseOptionalGuid(request.EntryId);
                break;
        }

        return _mediator.Send(command);
    }

    /// <summary>
    /// Парсит строку как Guid. Пустая строка интерпретируется как «корень» (null).
    /// </summary>
    private static Guid? ParseOptionalGuid(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
    }
}
