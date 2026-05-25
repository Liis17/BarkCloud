using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrashToCloudFileEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId_Name",
                table: "CloudFileEntries");

            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "CloudFileEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "CloudFileEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PurgeAt",
                table: "CloudFileEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId_Name",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "DirectoryId", "Name" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "FileId" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_PurgeAt",
                table: "CloudFileEntries",
                column: "PurgeAt",
                filter: "\"IsDeleted\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId_Name",
                table: "CloudFileEntries");

            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries");

            migrationBuilder.DropIndex(
                name: "IX_CloudFileEntries_PurgeAt",
                table: "CloudFileEntries");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "CloudFileEntries");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "CloudFileEntries");

            migrationBuilder.DropColumn(
                name: "PurgeAt",
                table: "CloudFileEntries");

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_DirectoryId_Name",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "DirectoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CloudFileEntries_OwnerId_FileId",
                table: "CloudFileEntries",
                columns: new[] { "OwnerId", "FileId" },
                unique: true);
        }
    }
}
