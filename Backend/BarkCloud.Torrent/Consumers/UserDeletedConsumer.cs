using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Torrent.Consumers;

/// <summary>
/// Удаление аккаунта: снимает торренты пользователя из движка, удаляет его папку закачки
/// и записи в БД.
/// </summary>
public class UserDeletedConsumer(
    TorrentContext context,
    TorrentEngineService engine,
    IConfiguration configuration,
    MetricsCollector metrics,
    ILogger<UserDeletedConsumer> logger)
    : IConsumer<UserDeleted>
{
    public async Task Consume(ConsumeContext<UserDeleted> consumeContext)
    {
        var userId = consumeContext.Message.UserId;
        metrics.Increment("rabbitmq_events_consumed");

        var torrents = await context.Torrents.Where(t => t.UserId == userId).ToListAsync();

        foreach (var t in torrents)
            await engine.RemoveAsync(t.Id, deleteData: true);

        // Папка пользователя целиком (на случай осиротевших файлов).
        var downloadPath = configuration["Torrent:DownloadPath"] ?? "/mnt/torrents";
        var userDir = Path.Combine(downloadPath, userId.ToString());
        if (Directory.Exists(userDir))
        {
            try { Directory.Delete(userDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить папку торрентов пользователя {UserId}", userId); }
        }

        await context.Torrents.Where(t => t.UserId == userId).ExecuteDeleteAsync();

        logger.LogInformation("Торренты пользователя {UserId} удалены: {Count}", userId, torrents.Count);
    }
}
