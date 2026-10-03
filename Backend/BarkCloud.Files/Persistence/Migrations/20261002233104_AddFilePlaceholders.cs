using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilePlaceholders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilePlaceholders",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFilePreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    Colors = table.Column<string[]>(type: "text[]", nullable: false),
                    AspectRatio = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilePlaceholders", x => x.FileId);
                    table.CheckConstraint("CK_FilePlaceholders_ColorCount", "cardinality(\"Colors\") = 9");
                    table.ForeignKey(
                        name: "FK_FilePlaceholders_FilePreviews_SourceFilePreviewId",
                        column: x => x.SourceFilePreviewId,
                        principalTable: "FilePreviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FilePlaceholders_UploadedFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "UploadedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilePlaceholders_SourceFilePreviewId",
                table: "FilePlaceholders",
                column: "SourceFilePreviewId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilePlaceholders");
        }
    }
}
