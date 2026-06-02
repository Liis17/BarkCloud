using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveFolderShare;

public class ResolveFolderShareCommand : IRequest<ResolveFolderShareResponse>
{
    public string Token { get; set; } = string.Empty;

    /// <summary>Подпапка внутри расшаренного поддерева (пусто = корень расшаренной папки).</summary>
    public string Dir { get; set; } = string.Empty;
}
