using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaKindAndAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_FileId",
                table: "CloudFileEntries");

            migrationBuilder.AddColumn<int>(
                name: "MediaKind",
                table: "UploadedFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Бэкафилл MediaKind для уже загруженных файлов: всё, что имеет размеры изображения,
            // помечаем как Photo (1). Остальные остаются Other (0).
            migrationBuilder.Sql(
                "UPDATE \"UploadedFiles\" SET \"MediaKind\" = 1 WHERE \"ImageWidth\" IS NOT NULL AND \"ImageWidth\" > 0;");

            migrationBuilder.CreateTable(
                name: "AlbumItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Albums",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CoverFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Albums", x => x.Id);
                });

            // Инвариант «одна директория на файл»: перед созданием уникального индекса
            // дедуплицируем существующие записи, оставляя по одной (самой ранней) на (OwnerId, FileId).
            migrationBuilder.Sql(@"
DELETE FROM ""CloudFileEntries"" e
USING (
    SELECT ""Id"", ROW_NUMBER() OVER (
        PARTITION BY ""OwnerId"", ""FileId"" ORDER BY ""CreatedAt"", ""Id""
    ) AS rn
    FROM ""CloudFileEntries""
) d
WHERE e.""Id"" = d.""Id"" AND d.rn > 1;");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlbumItems_AlbumId_AddedAt",
                table: "AlbumItems",
                columns: new[] { "AlbumId", "AddedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumItems_AlbumId_FileId",
                table: "AlbumItems",
                columns: new[] { "AlbumId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlbumItems_FileId",
                table: "AlbumItems",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_OwnerId_Name",
                table: "Albums",
                columns: new[] { "OwnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Albums_OwnerId_UpdatedAt",
                table: "Albums",
                columns: new[] { "OwnerId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumItems");

            migrationBuilder.DropTable(
                name: "Albums");

            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries");

            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "UploadedFiles");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_FileId",
                table: "CloudFileEntries",
                column: "FileId");
        }
    }
}
