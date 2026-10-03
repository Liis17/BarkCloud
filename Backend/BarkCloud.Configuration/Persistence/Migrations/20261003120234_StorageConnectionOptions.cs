using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageConnectionOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForcePathStyle",
                table: "StorageProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "StorageProfiles",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForcePathStyle",
                table: "StorageProfiles");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "StorageProfiles");
        }
    }
}
