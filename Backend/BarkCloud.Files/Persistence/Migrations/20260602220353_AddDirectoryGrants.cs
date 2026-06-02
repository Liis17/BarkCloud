using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectoryGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientId = table.Column<long>(type: "bigint", nullable: false),
                    DirectoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGrants_DirectoryId",
                table: "DirectoryGrants",
                column: "DirectoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGrants_OwnerId_DirectoryId_RecipientId",
                table: "DirectoryGrants",
                columns: new[] { "OwnerId", "DirectoryId", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGrants_RecipientId_CreatedAt",
                table: "DirectoryGrants",
                columns: new[] { "RecipientId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectoryGrants");
        }
    }
}
