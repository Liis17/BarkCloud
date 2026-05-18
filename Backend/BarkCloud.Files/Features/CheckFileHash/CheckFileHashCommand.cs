using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.CheckFileHash;

public class CheckFileHashCommand : IRequest<CheckFileHashResponse>
{
    /// <summary>
    /// SHA256 hash of the file (hex string, 64 characters).
    /// </summary>
    public string FileHash { get; set; } = string.Empty;
}
