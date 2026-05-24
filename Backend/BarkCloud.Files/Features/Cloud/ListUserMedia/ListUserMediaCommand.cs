using BarkCloud.Proto.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.ListUserMedia;

public class ListUserMediaCommand : IRequest<ListUserMediaResponse>
{
    /// <summary>
    /// Категория медиа для листинга (Photo / Video).
    /// </summary>
    public DomainMediaKind Kind { get; set; }

    public int Limit { get; set; }

    /// <summary>
    /// Курсор по дате создания (exclusive). null означает «с самых новых».
    /// </summary>
    public DateTime? CursorCreatedAt { get; set; }

    /// <summary>
    /// Tie-breaker для записей с одинаковым CreatedAt. null если курсор не задан.
    /// </summary>
    public Guid? CursorFileId { get; set; }
}
