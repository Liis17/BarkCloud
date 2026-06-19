using Microsoft.Extensions.Configuration;

namespace BarkCloud.GrpcServer;

public static class ConfigurationFeatureExtensions
{
    /// <summary>
    /// Включена ли почта на сервере. Вычисляется Configuration-сервисом из наличия SMTP-полей
    /// (Email:Host/Port/SenderEmail/SenderPassword) и раздаётся всем сервисам как Features:EmailEnabled.
    /// Дефолт true — обратная совместимость, если Configuration ещё не пересобран и ключа нет.
    /// </summary>
    public static bool EmailEnabled(this IConfiguration configuration)
        => configuration.GetValue("Features:EmailEnabled", true);

    /// <summary>
    /// Разрешена ли регистрация новых аккаунтов. Хранится в Configuration как
    /// Features:RegistrationEnabled и по умолчанию включена для обратной совместимости.
    /// </summary>
    public static bool RegistrationEnabled(this IConfiguration configuration)
        => configuration.GetValue("Features:RegistrationEnabled", true);
}
