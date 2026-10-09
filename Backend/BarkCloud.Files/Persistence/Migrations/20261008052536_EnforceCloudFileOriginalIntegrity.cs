using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceCloudFileOriginalIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Исторические ссылки без оригинала уже не восстановимы. Очищаем только их;
            // блокировки исключают новые изменения между очисткой и проверкой FK.
            migrationBuilder.Sql("LOCK TABLE \"UploadedFiles\", \"CloudFileEntries\" IN SHARE ROW EXCLUSIVE MODE;");
            migrationBuilder.Sql("""
                DELETE FROM "CloudFileEntries" AS entry
                WHERE NOT EXISTS (
                    SELECT 1 FROM "UploadedFiles" AS original WHERE original."Id" = entry."FileId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_FileId",
                table: "CloudFileEntries",
                column: "FileId");

            migrationBuilder.AddForeignKey(
                name: "FK_CloudFileEntries_UploadedFiles_FileId",
                table: "CloudFileEntries",
                column: "FileId",
                principalTable: "UploadedFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CloudFileEntries_UploadedFiles_FileId",
                table: "CloudFileEntries");

            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_FileId",
                table: "CloudFileEntries");
        }
    }
}
