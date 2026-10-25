using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTags_OwnerId_NormalizedName",
                table: "UserTags",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "UserTags" ("Id", "OwnerId", "Name", "NormalizedName", "Color")
                SELECT gen_random_uuid(), "OwnerId", min("Value" COLLATE "C"), "NormalizedValue", '#6750A4'
                FROM "FileTags"
                GROUP BY "OwnerId", "NormalizedValue";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTags");
        }
    }
}
