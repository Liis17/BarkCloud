using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueCloudDirectories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE \"CloudDirectories\" IN SHARE ROW EXCLUSIVE MODE;");
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicate_roots text;
                    duplicate_systems text;
                BEGIN
                    SELECT string_agg(format('OwnerId=%s, Name=%L, ids=%s', "OwnerId", "Name", directory_ids), '; ')
                    INTO duplicate_roots
                    FROM (
                        SELECT "OwnerId", "Name", array_agg("Id" ORDER BY "Id") AS directory_ids
                        FROM "CloudDirectories"
                        WHERE "ParentId" IS NULL
                        GROUP BY "OwnerId", "Name"
                        HAVING count(*) > 1
                        ORDER BY "OwnerId", "Name"
                        LIMIT 20
                    ) duplicates;

                    SELECT string_agg(format('OwnerId=%s, SystemKind=%s, ids=%s', "OwnerId", "SystemKind", directory_ids), '; ')
                    INTO duplicate_systems
                    FROM (
                        SELECT "OwnerId", "SystemKind", array_agg("Id" ORDER BY "Id") AS directory_ids
                        FROM "CloudDirectories"
                        WHERE "SystemKind" <> 0
                        GROUP BY "OwnerId", "SystemKind"
                        HAVING count(*) > 1
                        ORDER BY "OwnerId", "SystemKind"
                        LIMIT 20
                    ) duplicates;

                    IF duplicate_roots IS NOT NULL OR duplicate_systems IS NOT NULL THEN
                        RAISE EXCEPTION 'F16: обнаружены дубли папок. Корневые=[%]; Системные=[%]. Разрешите дубли перед миграцией (показано до 20 групп каждого типа).',
                            coalesce(duplicate_roots, ''), coalesce(duplicate_systems, '');
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories");

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "SystemKind" },
                unique: true,
                filter: "\"SystemKind\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_Name",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "Name" },
                unique: true,
                filter: "\"ParentId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CloudDirectories_OwnerId_Name",
                table: "CloudDirectories");

            migrationBuilder.DropIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories");

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "SystemKind" },
                filter: "\"SystemKind\" <> 0");
        }
    }
}
