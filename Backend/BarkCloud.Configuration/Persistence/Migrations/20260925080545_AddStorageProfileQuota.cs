using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageProfileQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "StorageProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "StorageProfiles");
        }
    }
}
