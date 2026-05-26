using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.CheckFileHashes;

public class CheckFileHashesCommand : IRequest<CheckFileHashesResponse>
{
    /// <summary>
    /// SHA256-хеши файлов (hex строки по 64 символа).
    /// </summary>
    public IReadOnlyList<string> FileHashes { get; init; } = [];
}
