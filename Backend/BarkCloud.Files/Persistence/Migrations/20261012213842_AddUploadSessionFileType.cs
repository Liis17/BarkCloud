using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadSessionFileType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FileType",
                table: "UploadSessions",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.Sql("""
                UPDATE "UploadSessions" AS session
                SET "FileType" = file."Type"
                FROM "UploadedFiles" AS file
                WHERE session."FileId" = file."Id"
                  AND file."Type" IN (1, 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileType",
                table: "UploadSessions");
        }
    }
}
