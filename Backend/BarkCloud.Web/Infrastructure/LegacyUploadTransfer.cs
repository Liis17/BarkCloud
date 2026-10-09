namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// HttpClient legacy-загрузки Web → Files (<c>POST /upload/{id}</c>). Отдельно от <c>"files-upload"</c>,
/// который обслуживает прокси скачивания/просмотра и сохраняет срок по умолчанию.
/// </summary>
public static class LegacyUploadTransfer
{
    public const string ClientName = "files-legacy-upload";

    // = nginx proxy_read_timeout 7200s: дольше ответ до браузера всё равно не дойдёт.
    public static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public static IServiceCollection AddLegacyUploadTransferClient(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, c => c.Timeout = Timeout);
        return services;
    }
}
