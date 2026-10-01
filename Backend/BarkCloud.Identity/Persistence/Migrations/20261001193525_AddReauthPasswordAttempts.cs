using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReauthPasswordAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReauthPasswordAttempts",
                table: "AuthUserProperties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReauthPasswordWindowEndsAt",
                table: "AuthUserProperties",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReauthPasswordAttempts",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "ReauthPasswordWindowEndsAt",
                table: "AuthUserProperties");
        }
    }
}
