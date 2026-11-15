using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Proto.Users;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Снимает владельца с файлов аватаров (<see cref="UploadFileType.UserAvatar"/>), которые больше не
/// являются его текущим аватаром или превью в профиле Users. После этого блобы без владельцев удаляет
/// <see cref="OrphanBlobCleanupService"/>.
/// </summary>
/// <remarks>
/// Источник правды о текущем аватаре — профиль в Users: сверка покрывает оба пути загрузки (Web через
/// UploadAvatarServer и клиентский USER_AVATAR + SetProfilePicture), замену, удаление и сбой между
/// загрузкой и назначением, не завися от порядка событий. Файлы моложе <see cref="Grace"/> не трогаются —
/// клиент может ещё не успеть назначить загруженный файл. Ошибка запроса к Users прерывает проход до
/// любых изменений; пользователи, которых Users не вернул (удалены), пропускаются — их файлы освобождает
/// <see cref="Consumers.UserDeletedConsumer"/>.
/// </remarks>
public sealed class StaleAvatarReleaser(
    FilesContext context,
    UsersServerApi.UsersServerApiClient users,
    ILogger<StaleAvatarReleaser> logger)
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private const int UsersBatchSize = 200;

    /// <summary>Освобождает устаревшие аватары, загруженные раньше <paramref name="cutoff"/>. Возвращает число снятых владельцев.</summary>
    public async Task<int> ReleaseAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var candidates = await context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Type == UploadFileType.UserAvatar
                        && f.Uploaders.Count > 0
                        && f.UploadedAt != null
                        && f.UploadedAt < cutoff)
            .Select(f => new { f.Id, f.Uploaders })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return 0;

        // Файлы, на которые ссылаются облачные записи или альбомы, не трогаем — это уже не только аватар.
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var referenced = (await context.CloudFileEntries.Where(e => candidateIds.Contains(e.FileId)).Select(e => e.FileId)
                .Union(context.AlbumItems.Where(a => candidateIds.Contains(a.FileId)).Select(a => a.FileId))
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // Сначала получаем текущие аватары всех владельцев: ошибка Users не должна оставить проход наполовину.
        var current = new Dictionary<long, HashSet<Guid>>();
        foreach (var chunk in candidates.SelectMany(c => c.Uploaders).Distinct().Chunk(UsersBatchSize))
        {
            var response = await users.ListByIdsAsync(
                new ListByIdsRequest { Ids = { chunk } }, cancellationToken: cancellationToken);
            foreach (var user in response.Users)
            {
                if (TryGetAvatarFileIds(user, out var keep))
                    current[user.Id] = keep;
                else
                    logger.LogWarning("Не удалось разобрать ссылку на аватар пользователя {UserId}, его файлы пропущены", user.Id);
            }
        }

        var released = 0;
        foreach (var candidate in candidates)
        {
            if (referenced.Contains(candidate.Id))
                continue;
            foreach (var userId in candidate.Uploaders)
            {
                if (!current.TryGetValue(userId, out var keep) || keep.Contains(candidate.Id))
                    continue;
                await context.RemoveUploaderAsync(candidate.Id, userId, cancellationToken);
                released++;
            }
        }

        if (released > 0)
            logger.LogInformation("Освобождено устаревших файлов аватаров: {Count}", released);
        return released;
    }

    /// <summary>
    /// Идентификаторы файлов текущего аватара и превью из ссылок вида <c>.../download/{fileId}</c>.
    /// Сравнение по id, а не по строке: публичный base URL зависит от конфигурации. Непустая ссылка
    /// без id — неизвестный формат, такого пользователя пропускаем.
    /// </summary>
    private static bool TryGetAvatarFileIds(User user, out HashSet<Guid> fileIds)
    {
        fileIds = [];
        foreach (var url in new[] { user.ProfilePicture, user.ProfilePicturePreview })
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var path = url.Split('?', '#')[0].TrimEnd('/');
            if (!Guid.TryParse(path[(path.LastIndexOf('/') + 1)..], out var fileId))
                return false;
            fileIds.Add(fileId);
        }
        return true;
    }
}
