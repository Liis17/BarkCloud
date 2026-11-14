using System.Text.RegularExpressions;

using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.SetDirectoryAppearance;

public partial class SetDirectoryAppearanceCommandHandler : IRequestHandler<SetDirectoryAppearanceCommand, DirectoryInfo>
{
    private const int MaxIconKeyLength = 64;

    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;

    public SetDirectoryAppearanceCommandHandler(ICloudHierarchyStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<DirectoryInfo> Handle(SetDirectoryAppearanceCommand request, CancellationToken cancellationToken)
    {
        // Пак иконок серверу неизвестен: проверяем только формат, клиент сам откатывается на стандартную папку.
        var iconKey = (request.IconKey ?? string.Empty).Trim();
        var iconColor = (request.IconColor ?? string.Empty).Trim().ToUpperInvariant();
        if (iconKey.Length > 0 && (iconKey.Length > MaxIconKeyLength || !IconKeyRegex().IsMatch(iconKey)))
            throw new DirectoryAppearanceInvalidException();
        if (iconColor.Length > 0 && !IconColorRegex().IsMatch(iconColor))
            throw new DirectoryAppearanceInvalidException();

        var directory = await _storage.GetDirectory(request.DirectoryId, cancellationToken);
        if (directory is null)
            throw new DirectoryNotFoundException();
        if (directory.OwnerId != _userContext.UserId)
            throw new CloudAccessDeniedException();

        directory.IconKey = iconKey.Length > 0 ? iconKey : null;
        directory.IconColor = iconColor.Length > 0 ? iconColor : null;
        await _storage.UpdateDirectory(directory, cancellationToken);

        return new DirectoryInfo
        {
            Id = directory.Id.ToString(),
            ParentId = directory.ParentId?.ToString() ?? string.Empty,
            Name = directory.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.UpdatedAt, DateTimeKind.Utc)),
            IconKey = directory.IconKey ?? string.Empty,
            IconColor = directory.IconColor ?? string.Empty
        };
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IconKeyRegex();

    [GeneratedRegex("^#[0-9A-F]{6}$")]
    private static partial Regex IconColorRegex();
}
