using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudDirectorySystemKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SystemKind",
                table: "CloudDirectories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories",
                columns: new[] { "OwnerId", "SystemKind" },
                filter: "\"SystemKind\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CloudDirectories_OwnerId_SystemKind",
                table: "CloudDirectories");

            migrationBuilder.DropColumn(
                name: "SystemKind",
                table: "CloudDirectories");
        }
    }
}
