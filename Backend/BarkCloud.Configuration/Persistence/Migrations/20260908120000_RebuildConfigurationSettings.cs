using BarkCloud.Configuration.Infrastructure;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations;

[DbContext(typeof(ConfigurationContext))]
[Migration("20260908120000_RebuildConfigurationSettings")]
public partial class RebuildConfigurationSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "Configurations", newName: "ConfigurationsLegacy");

        foreach (var table in new[]
                 {
                     "GlobalSettings", "IdentitySettings", "UsersSettings", "NotificationSettings",
                     "FilesSettings", "WebSettings", "TorrentSettings"
                 })
            CreateSettingsTable(migrationBuilder, table);

        migrationBuilder.CreateTable(
            name: "ReservedNames",
            columns: table => new
            {
                Name = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ReservedNames", row => row.Name));

        migrationBuilder.CreateTable(
            name: "SettingsHistory",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                SettingsTable = table.Column<string>(type: "text", nullable: false),
                Key = table.Column<string>(type: "text", nullable: false),
                PreviousValue = table.Column<string>(type: "text", nullable: false),
                NewValue = table.Column<string>(type: "text", nullable: false),
                ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ChangedBy = table.Column<string>(type: "text", nullable: false),
                ChangedFrom = table.Column<string>(type: "text", nullable: false),
                ChangeKind = table.Column<string>(type: "text", nullable: false),
                SourceRevisionId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SettingsHistory", row => row.Id);
                table.ForeignKey(
                    name: "FK_SettingsHistory_SettingsHistory_SourceRevisionId",
                    column: row => row.SourceRevisionId,
                    principalTable: "SettingsHistory",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StorageProfiles",
            columns: table => new
            {
                ProfileId = table.Column<string>(type: "text", nullable: false),
                Role = table.Column<string>(type: "text", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                ServiceUrl = table.Column<string>(type: "text", nullable: false),
                AccessKey = table.Column<string>(type: "text", nullable: false),
                SecretKey = table.Column<string>(type: "text", nullable: false),
                BucketName = table.Column<string>(type: "text", nullable: false),
                IsR2 = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IsLegacy = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: false),
                CreatedFrom = table.Column<string>(type: "text", nullable: false),
                EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EditedBy = table.Column<string>(type: "text", nullable: false),
                EditedFrom = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_StorageProfiles", row => row.ProfileId));

        migrationBuilder.CreateTable(
            name: "StorageProfileRevisions",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ProfileId = table.Column<string>(type: "text", nullable: false),
                PreviousValue = table.Column<string>(type: "text", nullable: false),
                NewValue = table.Column<string>(type: "text", nullable: false),
                ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ChangedBy = table.Column<string>(type: "text", nullable: false),
                ChangedFrom = table.Column<string>(type: "text", nullable: false),
                ChangeKind = table.Column<string>(type: "text", nullable: false),
                SourceRevisionId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorageProfileRevisions", row => row.Id);
                table.ForeignKey(
                    name: "FK_StorageProfileRevisions_StorageProfileRevisions_SourceRevisionId",
                    column: row => row.SourceRevisionId,
                    principalTable: "StorageProfileRevisions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StorageProfileRevisions_StorageProfiles_ProfileId",
                    column: row => row.ProfileId,
                    principalTable: "StorageProfiles",
                    principalColumn: "ProfileId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SettingsHistory_SettingsTable_Key_ChangedAt_Id",
            table: "SettingsHistory",
            columns: new[] { "SettingsTable", "Key", "ChangedAt", "Id" },
            descending: new[] { false, false, true, true });
        migrationBuilder.CreateIndex(
            name: "IX_SettingsHistory_SourceRevisionId",
            table: "SettingsHistory",
            column: "SourceRevisionId");
        migrationBuilder.CreateIndex(
            name: "IX_StorageProfiles_Role_IsActive",
            table: "StorageProfiles",
            columns: new[] { "Role", "IsActive" });
        migrationBuilder.CreateIndex(
            name: "IX_StorageProfiles_Role_Version",
            table: "StorageProfiles",
            columns: new[] { "Role", "Version" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_StorageProfileRevisions_ProfileId_ChangedAt_Id",
            table: "StorageProfileRevisions",
            columns: new[] { "ProfileId", "ChangedAt", "Id" },
            descending: new[] { false, true, true });
        migrationBuilder.CreateIndex(
            name: "IX_StorageProfileRevisions_SourceRevisionId",
            table: "StorageProfileRevisions",
            column: "SourceRevisionId");

        MigrateScope(migrationBuilder, "GlobalSettings", 0, """
            ("Section", "Key") IN (
                ('JwtSettings','SecretKey'), ('JwtSettings','Issuer'), ('JwtSettings','Audience'), ('JwtSettings','ExpiryMinutes'),
                ('RabbitMQ','Host'), ('RabbitMQ','Username'), ('RabbitMQ','Password'), ('RabbitMQ','VirtualHost'),
                ('Seq','ServerUrl'), ('Features','RegistrationEnabled'))
            """);
        MigrateScope(migrationBuilder, "IdentitySettings", 1, """
            ("Section", "Key") IN (
                ('RunSettings','Port'), ('IdentityDb',''), ('UsersService','Host'), ('UsersService','Token'),
                ('ExternalEndpoint','Host'), ('WebAuthn','RpId'), ('WebAuthn','ServerName'), ('WebAuthn','Origins'))
            """);
        MigrateScope(migrationBuilder, "UsersSettings", 2, """
            ("Section", "Key") IN (
                ('RunSettings','Port'), ('UsersDb',''), ('FilesService','Host'), ('FilesService','Token'),
                ('ExternalEndpoint','Host'))
            """);
        MigrateScope(migrationBuilder, "NotificationSettings", 3, """
            ("Section", "Key") IN (
                ('RunSettings','Port'), ('Email','Host'), ('Email','Port'), ('Email','SenderEmail'), ('Email','SenderPassword'))
            """);
        MigrateScope(migrationBuilder, "FilesSettings", 5, """
            ("Section", "Key") IN (
                ('RunSettings','Port'), ('RunSettings','Http1Port'), ('FilesDb',''), ('UsersService','Host'),
                ('UsersService','Token'), ('ExternalEndpoint','Host'), ('TempFiles','ExpiresAt'))
            """);
        MigrateScope(migrationBuilder, "WebSettings", 6, """
            ("Section", "Key") IN (
                ('IdentityService','Host'), ('UsersService','Host'), ('FilesService','Host'), ('TorrentService','Host'))
            """);
        MigrateScope(migrationBuilder, "TorrentSettings", 7, """
            ("Section", "Key") IN (
                ('RunSettings','Port'), ('RunSettings','Http1Port'), ('TorrentDb',''), ('UsersService','Host'),
                ('UsersService','Token'), ('FilesService','Host'), ('FilesService','Token'), ('ExternalEndpoint','Host'),
                ('Torrent','DownloadPath'), ('Torrent','PeerPort'))
            """);

        migrationBuilder.Sql("""
            WITH ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY "ServiceId", "Section", "Key"
                    ORDER BY "EditedAt" DESC, "Id" DESC) AS rn
                FROM "ConfigurationsLegacy"
            )
            INSERT INTO "ReservedNames" ("Name")
            SELECT DISTINCT lower(trim(name))
            FROM ranked
            CROSS JOIN LATERAL regexp_split_to_table("Value", ',') AS name
            WHERE rn = 1 AND "ServiceId" = 0 AND "Section" = 'ReservedNames' AND "Key" = 'Usernames'
              AND trim(name) <> ''
            ON CONFLICT ("Name") DO NOTHING;
            """);

        MigrateLegacyProfile(migrationBuilder, "user-avatars", "user-avatars-old-v1", "user-avatars-old");
        MigrateLegacyProfile(migrationBuilder, "cloud-files", "cloud-files-old-v1", "cloud-files-old");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ReservedNames");
        migrationBuilder.DropTable(name: "SettingsHistory");
        migrationBuilder.DropTable(name: "StorageProfileRevisions");
        migrationBuilder.DropTable(name: "StorageProfiles");
        foreach (var table in new[]
                 {
                     "GlobalSettings", "IdentitySettings", "UsersSettings", "NotificationSettings",
                     "FilesSettings", "WebSettings", "TorrentSettings"
                 })
            migrationBuilder.DropTable(name: table);
        migrationBuilder.RenameTable(name: "ConfigurationsLegacy", newName: "Configurations");
    }

    private static void CreateSettingsTable(MigrationBuilder migrationBuilder, string tableName)
    {
        migrationBuilder.CreateTable(
            name: tableName,
            columns: table => new
            {
                Key = table.Column<string>(type: "text", nullable: false),
                Value = table.Column<string>(type: "text", nullable: false),
                EditedBy = table.Column<string>(type: "text", nullable: false),
                EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey($"PK_{tableName}", row => row.Key));
    }

    private static void MigrateScope(
        MigrationBuilder migrationBuilder,
        string tableName,
        int serviceId,
        string knownPredicate)
    {
        if (serviceId != 0)
        {
            knownPredicate = $"({knownPredicate}) OR " + """
                ("Section", "Key") IN (
                    ('JwtSettings','SecretKey'), ('JwtSettings','Issuer'), ('JwtSettings','Audience'), ('JwtSettings','ExpiryMinutes'),
                    ('RabbitMQ','Host'), ('RabbitMQ','Username'), ('RabbitMQ','Password'), ('RabbitMQ','VirtualHost'),
                    ('Seq','ServerUrl'), ('Features','RegistrationEnabled'))
                """;
        }
        var ranked = $$"""
            WITH ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY "ServiceId", "Section", "Key"
                    ORDER BY "EditedAt" DESC, "Id" DESC) AS rn
                FROM "ConfigurationsLegacy"
            )
            """;
        migrationBuilder.Sql(ranked + $$"""
            INSERT INTO "{{tableName}}" ("Key", "Value", "EditedBy", "EditedAt")
            SELECT CASE WHEN "Key" = '' THEN "Section" ELSE "Section" || ':' || "Key" END,
                   "Value", "EditedBy", "EditedAt"
            FROM ranked
            WHERE rn = 1 AND "ServiceId" = {{serviceId}} AND ({{knownPredicate}});
            """);
        migrationBuilder.Sql(ranked + $$"""
            INSERT INTO "SettingsHistory"
                ("SettingsTable", "Key", "PreviousValue", "NewValue", "ChangedAt", "ChangedBy", "ChangedFrom", "ChangeKind")
            SELECT '{{tableName}}',
                   CASE WHEN "Key" = '' THEN "Section" ELSE "Section" || ':' || "Key" END,
                   '', "Value", "EditedAt", "EditedBy", "EditedFrom", 'Migration'
            FROM ranked
            WHERE rn = 1 AND "ServiceId" = {{serviceId}} AND ({{knownPredicate}});
            """);
    }

    private static void MigrateLegacyProfile(
        MigrationBuilder migrationBuilder,
        string legacyBucketId,
        string profileId,
        string role)
    {
        migrationBuilder.Sql($$"""
            WITH ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY "ServiceId", "Section", "Key"
                    ORDER BY "EditedAt" DESC, "Id" DESC) AS rn
                FROM "ConfigurationsLegacy"
            ), values AS (
                SELECT
                    max("Value") FILTER (WHERE "Key" = 'ServiceUrl') AS service_url,
                    max("Value") FILTER (WHERE "Key" = 'AccessKey') AS access_key,
                    max("Value") FILTER (WHERE "Key" = 'SecretKey') AS secret_key,
                    max("Value") FILTER (WHERE "Key" = 'BucketName') AS bucket_name,
                    max("EditedAt") AS edited_at,
                    max("EditedBy") AS edited_by,
                    max("EditedFrom") AS edited_from
                FROM ranked
                WHERE rn = 1 AND "ServiceId" = 5 AND "Section" = 'S3Buckets:{{legacyBucketId}}'
            )
            INSERT INTO "StorageProfiles"
                ("ProfileId", "Role", "Version", "ServiceUrl", "AccessKey", "SecretKey", "BucketName",
                 "IsR2", "IsActive", "IsLegacy", "CreatedAt", "CreatedBy", "CreatedFrom", "EditedAt", "EditedBy", "EditedFrom")
            SELECT '{{profileId}}', '{{role}}', 1, service_url, access_key, secret_key, bucket_name,
                   position('.r2.cloudflarestorage.com' in lower(service_url)) > 0, false, true,
                   edited_at, coalesce(edited_by, 'system'), coalesce(edited_from, 'migration'),
                   edited_at, coalesce(edited_by, 'system'), coalesce(edited_from, 'migration')
            FROM values
            WHERE service_url <> '' AND access_key <> '' AND secret_key <> '' AND bucket_name <> '';

            INSERT INTO "StorageProfileRevisions"
                ("ProfileId", "PreviousValue", "NewValue", "ChangedAt", "ChangedBy", "ChangedFrom", "ChangeKind")
            SELECT "ProfileId", '', jsonb_build_object(
                       'ProfileId', "ProfileId", 'Role', "Role", 'Version', "Version",
                       'ServiceUrl', "ServiceUrl", 'AccessKey', "AccessKey", 'SecretKey', "SecretKey",
                       'BucketName', "BucketName", 'IsR2', "IsR2", 'IsActive', "IsActive", 'IsLegacy', "IsLegacy")::text,
                   "EditedAt", "EditedBy", "EditedFrom", 'Migration'
            FROM "StorageProfiles" WHERE "ProfileId" = '{{profileId}}';
            """);
    }
}
