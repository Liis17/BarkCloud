using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations;

[DbContext(typeof(FilesContext))]
[Migration("20260908123000_AddStorageProfileId")]
public partial class AddStorageProfileId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StorageProfileId",
            table: "UploadedFiles",
            type: "text",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "UploadedFiles"
            SET "StorageProfileId" = CASE
                WHEN "Type" = 1 THEN 'user-avatars-old-v1'
                ELSE 'cloud-files-old-v1'
            END
            WHERE "StorageProfileId" IS NULL OR "StorageProfileId" = '';
            """);

        migrationBuilder.AlterColumn<string>(
            name: "StorageProfileId",
            table: "UploadedFiles",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "StorageProfileId", table: "UploadedFiles");
    }
}
