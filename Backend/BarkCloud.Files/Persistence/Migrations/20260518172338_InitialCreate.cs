using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileHashes",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileHashes", x => x.FileId);
                });

            migrationBuilder.CreateTable(
                name: "TempFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TempFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UploadedFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Uploaders = table.Column<List<long>>(type: "bigint[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Etag = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: true),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ImageWidth = table.Column<int>(type: "integer", nullable: true),
                    ImageHeight = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadedFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileHashes_Hash",
                table: "FileHashes",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_TempFiles_OriginalFileId",
                table: "TempFiles",
                column: "OriginalFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileHashes");

            migrationBuilder.DropTable(
                name: "TempFiles");

            migrationBuilder.DropTable(
                name: "UploadedFiles");
        }
    }
}
