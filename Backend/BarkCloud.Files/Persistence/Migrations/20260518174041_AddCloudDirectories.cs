using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudDirectories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudDirectories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudDirectories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudFileEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    DirectoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudFileEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_ParentId",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_ParentId_Name",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_FileId",
                table: "CloudFileEntries",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "DirectoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId_Name",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "DirectoryId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudDirectories");

            migrationBuilder.DropTable(
                name: "CloudFileEntries");
        }
    }
}
