using BarkCloud.Configuration.Infrastructure;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations;

/// <summary>
/// Removes legacy shared RabbitMQ credentials and root-credential PostgreSQL connection strings.
/// New values are seeded from service-specific deployment secrets on startup.
/// </summary>
[DbContext(typeof(ConfigurationContext))]
[Migration("20261028120000_IsolateServiceCredentials")]
public partial class IsolateServiceCredentials : Migration
{
    private const string LegacyCredentials = @"
        (""Key"" IN ('RabbitMQ:Username', 'RabbitMQ:Password'))
        OR (""SettingsTable"" = 'IdentitySettings' AND ""Key"" = 'IdentityDb')
        OR (""SettingsTable"" = 'UsersSettings' AND ""Key"" = 'UsersDb')
        OR (""SettingsTable"" = 'FilesSettings' AND ""Key"" = 'FilesDb')
        OR (""SettingsTable"" = 'TorrentSettings' AND ""Key"" = 'TorrentDb')";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            UPDATE "SettingsHistory" SET "SourceRevisionId" = NULL
            WHERE "SourceRevisionId" IN (
                SELECT "Id" FROM "SettingsHistory" WHERE {LegacyCredentials});
            DELETE FROM "SettingsHistory" WHERE {LegacyCredentials};
            """);

        migrationBuilder.Sql("""
            DELETE FROM "GlobalSettings" WHERE "Key" IN ('RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "IdentitySettings" WHERE "Key" IN ('IdentityDb', 'RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "UsersSettings" WHERE "Key" IN ('UsersDb', 'RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "FilesSettings" WHERE "Key" IN ('FilesDb', 'RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "TorrentSettings" WHERE "Key" IN ('TorrentDb', 'RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "NotificationSettings" WHERE "Key" IN ('RabbitMQ:Username', 'RabbitMQ:Password');
            DELETE FROM "WebSettings" WHERE "Key" IN ('RabbitMQ:Username', 'RabbitMQ:Password');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Old shared credentials are intentionally not restored.
    }
}
