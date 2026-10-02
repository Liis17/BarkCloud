namespace BarkCloud.Files.Services;

/// <summary>
/// Лимиты legacy-загрузки <c>POST /upload/{id}</c>. Биндятся из секции <c>Uploads:Legacy</c>;
/// значения по умолчанию подходят без заведения ключей в Configuration.
/// </summary>
public sealed class LegacyUploadOptions
{
    /// <summary>Потолок размера файла, если у пользователя нет квоты (или остаток квоты больше).</summary>
    public long MaxFileBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Потолок для аватаров: handler держит их целиком в памяти.</summary>
    public long MaxAvatarBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Сколько legacy-загрузок принимается одновременно.</summary>
    public int MaxConcurrent { get; set; } = 8;

    /// <summary>Сколько запрос ждёт свободный слот, прежде чем получит 503.</summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Бюджет байт, которые одновременно буферизуются под legacy-загрузки. Реальный расход диска
    /// до 2× от значения: буфер формы + собственная копия handler'а.
    /// </summary>
    public long MaxBufferedBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>Запас на multipart-обвязку (границы, заголовки части) сверх размера файла.</summary>
    public long MultipartOverheadBytes { get; set; } = 64 * 1024;

    /// <summary>Минимальная скорость чтения тела; медленнее <see cref="MinRateGrace"/> — обрыв.</summary>
    public double MinBytesPerSecond { get; set; } = 1024;

    public TimeSpan MinRateGrace { get; set; } = TimeSpan.FromSeconds(30);
}
