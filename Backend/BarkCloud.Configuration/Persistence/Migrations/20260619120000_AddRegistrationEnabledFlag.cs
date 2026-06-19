using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationEnabledFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "Configurations"
                    ("Section", "Key", "Value", "EditedAt", "EditedBy", "EditedFrom", "ServiceId")
                SELECT 'Features', 'RegistrationEnabled', 'true', NOW() AT TIME ZONE 'UTC', 'system', 'migration', 0
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "Configurations"
                    WHERE "Section" = 'Features'
                      AND "Key" = 'RegistrationEnabled'
                      AND "ServiceId" = 0
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "Configurations"
                WHERE "Section" = 'Features'
                  AND "Key" = 'RegistrationEnabled'
                  AND "ServiceId" = 0;
                """);
        }
    }
}