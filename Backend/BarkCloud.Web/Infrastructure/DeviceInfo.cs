using System.Text;

using BarkCloud.Shared.Auth;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Сведения об устройстве клиента, выводимые из HTTP-запроса браузера.
/// Identity.Auth требует device-name / os / app-name / app-version.
/// </summary>
public sealed record DeviceInfo(
    string DeviceName,
    string Os,
    string AppName,
    string AppVersion,
    string DeviceId,
    string Ip)
{
    /// <summary>
    /// gRPC-метаданные с device-заголовками (все значения base64, как ожидает
    /// RequestContextInterceptor на стороне сервисов).
    /// </summary>
    public Metadata ToMetadata()
    {
        var metadata = new Metadata();

        AddBase64(metadata, MetadataKeys.DeviceName, DeviceName);
        AddBase64(metadata, MetadataKeys.OsName, Os);
        AddBase64(metadata, MetadataKeys.AppName, AppName);
        AddBase64(metadata, MetadataKeys.AppVersion, AppVersion);

        if (!string.IsNullOrEmpty(DeviceId))
            AddBase64(metadata, MetadataKeys.DeviceId, DeviceId);

        if (!string.IsNullOrEmpty(Ip))
        {
            AddBase64(metadata, MetadataKeys.IpAddress, Ip);

            // Доверенный адрес для лимитов попыток в Identity (читается как X-Real-IP; значение не base64).
            // Web вызывает Identity напрямую, минуя nginx, поэтому без него все веб-пользователи
            // попали бы в одно «ведро» с адресом контейнера Web.
            metadata.Add(MetadataKeys.RealIp, Ip);
        }

        return metadata;
    }

    private static void AddBase64(Metadata metadata, string key, string value)
        => metadata.Add(key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
}
