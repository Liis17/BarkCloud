using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BarkCloud.Users.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBioPrivacyFirebaseToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirebaseToken",
                table: "UserDevices",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserPrivacies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ProfileVisibility = table.Column<int>(type: "integer", nullable: false),
                    EmailVisibility = table.Column<int>(type: "integer", nullable: false),
                    LastSeenVisibility = table.Column<int>(type: "integer", nullable: false),
                    SearchableByUsername = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPrivacies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPrivacies_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPrivacies_UserId",
                table: "UserPrivacies",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPrivacies");

            migrationBuilder.DropColumn(
                name: "Bio",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FirebaseToken",
                table: "UserDevices");
        }
    }
}
