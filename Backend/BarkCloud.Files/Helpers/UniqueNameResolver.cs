namespace BarkCloud.Files.Helpers;

/// <summary>
/// Разрешает коллизии имён в директории: возвращает имя, уникальное по предикату
/// <c>nameExists</c>. Если желаемое имя занято — добавляет суффикс " (1)", " (2)"…
/// перед расширением. Используется при загрузке (AttachFile) и восстановлении из корзины.
/// </summary>
public static class UniqueNameResolver
{
    public static async Task<string> ResolveAsync(
        string desired,
        Func<string, CancellationToken, Task<bool>> nameExists,
        CancellationToken cancellationToken = default)
    {
        if (!await nameExists(desired, cancellationToken))
            return desired;

        var dot = desired.LastIndexOf('.');
        var stem = dot > 0 ? desired[..dot] : desired;
        var ext = dot > 0 ? desired[dot..] : string.Empty;

        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!await nameExists(candidate, cancellationToken))
                return candidate;
        }

        // Крайний случай: добавляем уникальный суффикс.
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}
