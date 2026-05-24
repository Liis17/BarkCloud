using BarkCloud.Configuration.Domain;
using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Infrastructure;

/// <summary>
/// Список всех ожидаемых конфигурационных ключей.
/// Используется при первом запуске Configuration-сервиса для засева пустой таблицы.
/// Реальные значения заполняются <see cref="ConfigurationDefaultsPopulator"/> на основе известных дефолтов;
/// для остального остаются плейсхолдеры — их видно в БД, чтобы было ясно, что менять.
/// </summary>
internal static class ConfigurationSeed
{
    /// <summary>
    /// Ожидаемые записи: Section / Key / ServiceId.
    /// Value намеренно не задаётся — заполнится populator'ом или останется пустым/placeholder'ом.
    /// </summary>
    public static IReadOnlyList<ConfigurationItem> BuildSeedItems()
    {
        var now = DateTime.UtcNow;
        const string editedBy = "system";
        const string editedFrom = "seed";

        ConfigurationItem item(string section, string key, ServiceId serviceId) => new()
        {
            Section = section,
            Key = key,
            Value = "",
            ServiceId = serviceId,
            EditedAt = now,
            EditedBy = editedBy,
            EditedFrom = editedFrom,
        };

        var items = new List<ConfigurationItem>
        {
            // ─── Общие (ServiceId.Unknown) ──────────────────────────────────
            item("JwtSettings", "SecretKey",     ServiceId.Unknown),
            item("JwtSettings", "Issuer",        ServiceId.Unknown),
            item("JwtSettings", "Audience",      ServiceId.Unknown),
            item("JwtSettings", "ExpiryMinutes", ServiceId.Unknown),

            item("RabbitMQ", "Host",        ServiceId.Unknown),
            item("RabbitMQ", "Username",    ServiceId.Unknown),
            item("RabbitMQ", "Password",    ServiceId.Unknown),
            item("RabbitMQ", "VirtualHost", ServiceId.Unknown),

            item("Seq", "ServerUrl", ServiceId.Unknown),

            item("ReservedNames", "Usernames", ServiceId.Unknown),

            // ─── Identity ───────────────────────────────────────────────────
            item("RunSettings",       "Port",  ServiceId.Identity),
            item("IdentityDb",        "",      ServiceId.Identity),
            item("UsersService",      "Host",  ServiceId.Identity),
            item("UsersService",      "Token", ServiceId.Identity),
            item("ExternalEndpoint",  "Host",  ServiceId.Identity),

            // ─── Notification (consumer email-уведомлений; SMTP-настройки — секреты, заполняются вручную) ─
            item("RunSettings", "Port",           ServiceId.Notification),
            item("Email",       "Host",           ServiceId.Notification),
            item("Email",       "Port",           ServiceId.Notification),
            item("Email",       "SenderEmail",    ServiceId.Notification),
            item("Email",       "SenderPassword", ServiceId.Notification),

            // ─── Users ──────────────────────────────────────────────────────
            item("RunSettings",       "Port",  ServiceId.Users),
            item("UsersDb",           "",      ServiceId.Users),
            item("FilesService",      "Host",  ServiceId.Users),
            item("FilesService",      "Token", ServiceId.Users),
            item("ExternalEndpoint",  "Host",  ServiceId.Users),

            // ─── Files ──────────────────────────────────────────────────────
            item("RunSettings",       "Port",      ServiceId.Files),
            item("RunSettings",       "Http1Port", ServiceId.Files),
            item("FilesDb",           "",          ServiceId.Files),
            item("UsersService",      "Host",      ServiceId.Files),
            item("UsersService",      "Token",     ServiceId.Files),
            item("ExternalEndpoint",  "Host",      ServiceId.Files),
            item("TempFiles",         "ExpiresAt", ServiceId.Files),

            // ─── S3 Buckets (Files) ─────────────────────────────────────────
            item("S3Buckets:user-avatars", "ServiceUrl",     ServiceId.Files),
            item("S3Buckets:user-avatars", "AccessKey",      ServiceId.Files),
            item("S3Buckets:user-avatars", "SecretKey",      ServiceId.Files),
            item("S3Buckets:user-avatars", "BucketName",     ServiceId.Files),
            item("S3Buckets:user-avatars", "ForcePathStyle", ServiceId.Files),

            item("S3Buckets:cloud-files",  "ServiceUrl",     ServiceId.Files),
            item("S3Buckets:cloud-files",  "AccessKey",      ServiceId.Files),
            item("S3Buckets:cloud-files",  "SecretKey",      ServiceId.Files),
            item("S3Buckets:cloud-files",  "BucketName",     ServiceId.Files),
            item("S3Buckets:cloud-files",  "ForcePathStyle", ServiceId.Files),

            // ─── Web (веб-клиент → адреса микросервисов; JwtSettings берёт из общих) ─
            item("IdentityService", "Host", ServiceId.Web),
            item("UsersService",    "Host", ServiceId.Web),
            item("FilesService",    "Host", ServiceId.Web),
        };

        return items;
    }
}
