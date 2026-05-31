using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileGrants_FileId",
                table: "FileGrants",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileGrants_OwnerId_FileId_RecipientId",
                table: "FileGrants",
                columns: new[] { "OwnerId", "FileId", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileGrants_RecipientId_CreatedAt",
                table: "FileGrants",
                columns: new[] { "RecipientId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileGrants");
        }
    }
}
