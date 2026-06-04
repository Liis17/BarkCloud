using BarkCloud.Proto.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.DynamicFolder.ListDynamicFolderItems;

public class ListDynamicFolderItemsCommand : IRequest<ListDynamicFolderItemsResponse>
{
    /// <summary>"sys-recent" / "sys-large" / "sys-screenshots" или Guid пользовательской папки.</summary>
    public string FolderId { get; set; } = "";

    public int Limit { get; set; }

    public DateTime? CursorCreatedAt { get; set; }

    public Guid? CursorFileId { get; set; }

    public DomainMediaKind? KindFilter { get; set; }
}
