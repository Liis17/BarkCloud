using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilePreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewId",
                table: "UploadedFiles");

            migrationBuilder.CreateTable(
                name: "FilePreviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetWidth = table.Column<int>(type: "integer", nullable: false),
                    ActualWidth = table.Column<int>(type: "integer", nullable: false),
                    ActualHeight = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilePreviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilePreviews_OriginalFileId_TargetWidth",
                table: "FilePreviews",
                columns: new[] { "OriginalFileId", "TargetWidth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilePreviews_PreviewFileId",
                table: "FilePreviews",
                column: "PreviewFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilePreviews");

            migrationBuilder.AddColumn<Guid>(
                name: "PreviewId",
                table: "UploadedFiles",
                type: "uuid",
                nullable: true);
        }
    }
}
