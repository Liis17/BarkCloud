using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace BarkCloud.Torrent.Host;

/// <summary>
/// HTTP1-эндпоинт (порт Http1Port) для стриминга скачанных файлов с диска по Range.
/// Пер-пользовательская проверка владельца через JWT.
/// </summary>
[Authorize(Policy = nameof(TokenType.User))]
public class TorrentController : Controller
{
    private readonly UserContext _userContext;
    private readonly ITorrentStore _store;
    private readonly TorrentEngineService _engine;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public TorrentController(UserContext userContext, ITorrentStore store, TorrentEngineService engine)
    {
        _userContext = userContext;
        _store = store;
        _engine = engine;
    }

    [HttpGet("download/{torrentId}")]
    public async Task<IActionResult> Download([FromRoute] Guid torrentId, [FromQuery] int file)
    {
        var entity = await _store.Get(torrentId, _userContext.UserId);
        if (entity == null)
            return NotFound();

        var managed = _engine.Get(torrentId);
        string? fullPath = null;

        if (managed != null && file >= 0 && file < managed.Manager.Files.Count)
            fullPath = managed.Manager.Files[file].FullPath;
        else
        {
            var row = entity.Files.FirstOrDefault(f => f.Index == file);
            if (row != null)
                fullPath = Path.Combine(entity.SavePath, row.Path);
        }

        if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath))
            return NotFound();

        var name = Path.GetFileName(fullPath);
        if (!ContentTypes.TryGetContentType(name, out var contentType))
            contentType = "application/octet-stream";

        return PhysicalFile(fullPath, contentType, name, enableRangeProcessing: true);
    }
}
