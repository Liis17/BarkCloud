using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderShareLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FolderShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    DirectoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClickCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderShareLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderShareLinks_OwnerId_CreatedAt",
                table: "FolderShareLinks",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FolderShareLinks_OwnerId_DirectoryId",
                table: "FolderShareLinks",
                columns: new[] { "OwnerId", "DirectoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FolderShareLinks_Token",
                table: "FolderShareLinks",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderShareLinks");
        }
    }
}
