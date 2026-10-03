using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageWriteActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageWriteActivities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ProfileIdsJson = table.Column<string>(type: "text", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageWriteActivities", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageWriteActivities");
        }
    }
}
