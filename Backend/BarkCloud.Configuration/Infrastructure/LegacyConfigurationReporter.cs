using BarkCloud.Configuration.Catalog;
using BarkCloud.Shared.Identity;

using Microsoft.EntityFrameworkCore;

using System.Data;

namespace BarkCloud.Configuration.Infrastructure;

public static class LegacyConfigurationReporter
{
    public static async Task WarnAboutUnknownRowsAsync(
        ConfigurationContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT "ServiceId", "Section", "Key"
            FROM "ConfigurationsLegacy"
            ORDER BY "ServiceId", "Section", "Key"
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var unknown = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var serviceValue = reader.GetInt32(0);
            var section = reader.GetString(1);
            var key = reader.GetString(2);
            if (serviceValue == (int)ServiceId.Unknown && section == "ReservedNames" && key == "Usernames")
                continue;
            if (serviceValue == (int)ServiceId.Files && IsKnownLegacyS3Row(section, key))
                continue;
            if (!Enum.IsDefined(typeof(ServiceId), serviceValue))
            {
                unknown.Add($"[{serviceValue}] {section}:{key}");
                continue;
            }
            try
            {
                SettingsCatalog.Resolve((ServiceId)serviceValue, section, key);
            }
            catch (UnknownSettingException)
            {
                unknown.Add($"[{serviceValue}] {section}:{key}");
            }
        }

        if (unknown.Count > 0)
            logger.LogWarning(
                "В ConfigurationsLegacy оставлены неизвестные ключи, которые Configuration больше не читает: {Keys}",
                string.Join(", ", unknown));
    }

    private static bool IsKnownLegacyS3Row(string section, string key) =>
        section is "S3Buckets:user-avatars" or "S3Buckets:cloud-files"
        && key is "ServiceUrl" or "AccessKey" or "SecretKey" or "BucketName" or "ForcePathStyle";
}
