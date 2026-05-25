using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Consumers;

/// <summary>
/// Реагирует на удаление аккаунта (событие из сервиса Users): очищает облачные данные
/// пользователя — снимает его из Uploaders всех блобов (освобождает квоту) и удаляет
/// каталоги, файловые записи, альбомы и их элементы.
/// </summary>
/// <remarks>
/// Физическое удаление осиротевших блобов из S3 здесь не выполняется — это соответствует
/// поведению ручного удаления (DeleteFileEntry/DeleteDirectory), которое тоже только
/// декрементит Uploaders. Очистка orphan-блобов — отдельная фоновая задача.
/// </remarks>
public class UserDeletedConsumer(
    FilesContext context,
    MetricsCollector metrics,
    ILogger<UserDeletedConsumer> logger)
    : IConsumer<UserDeleted>
{
    public async Task Consume(ConsumeContext<UserDeleted> consumeContext)
    {
        var userId = consumeContext.Message.UserId;

        metrics.Increment("rabbitmq_events_consumed");
        metrics.Increment("user_deleted_received");

        logger.LogInformation("Получено событие удаления аккаунта: UserId={UserId}", userId);

        // Снимаем пользователя из Uploaders всех его блобов (оригиналы, превью, аватары).
        var files = await context.UploadedFiles
            .Where(f => f.Uploaders.Contains(userId))
            .ToListAsync();

        foreach (var file in files)
        {
            file.Uploaders.Remove(userId);
        }

        if (files.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        // Удаляем владельческие данные иерархии, альбомов и избранного.
        var entries = await context.CloudFileEntries.Where(x => x.OwnerId == userId).ExecuteDeleteAsync();
        var dirs = await context.CloudDirectories.Where(x => x.OwnerId == userId).ExecuteDeleteAsync();
        var albumItems = await context.AlbumItems.Where(x => x.OwnerId == userId).ExecuteDeleteAsync();
        var albums = await context.Albums.Where(x => x.OwnerId == userId).ExecuteDeleteAsync();
        var favorites = await context.FavoriteFiles.Where(x => x.OwnerId == userId).ExecuteDeleteAsync();

        metrics.Increment("accounts_cleaned_files");

        logger.LogInformation(
            "Данные Files для пользователя {UserId} очищены: блобов откреплено {Files}, записей {Entries}, папок {Dirs}, альбомов {Albums} (элементов {AlbumItems}), избранного {Favorites}",
            userId, files.Count, entries, dirs, albums, albumItems, favorites);
    }
}
