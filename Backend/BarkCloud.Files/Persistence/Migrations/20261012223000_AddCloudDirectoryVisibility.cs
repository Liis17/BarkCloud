using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudDirectoryVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EffectiveHideFromGallery",
                table: "CloudDirectories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EffectiveHideFromSearch",
                table: "CloudDirectories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HideFromGallery",
                table: "CloudDirectories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HideFromSearch",
                table: "CloudDirectories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InheritVisibility",
                table: "CloudDirectories",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveHideFromGallery",
                table: "CloudDirectories");

            migrationBuilder.DropColumn(
                name: "EffectiveHideFromSearch",
                table: "CloudDirectories");

            migrationBuilder.DropColumn(
                name: "HideFromGallery",
                table: "CloudDirectories");

            migrationBuilder.DropColumn(
                name: "HideFromSearch",
                table: "CloudDirectories");

            migrationBuilder.DropColumn(
                name: "InheritVisibility",
                table: "CloudDirectories");
        }
    }
}
