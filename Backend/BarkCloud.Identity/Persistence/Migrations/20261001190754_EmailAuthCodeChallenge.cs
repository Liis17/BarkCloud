using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailAuthCodeChallenge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailAuthCodeAttempts",
                table: "AuthUserProperties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailAuthCodeExpiresAt",
                table: "AuthUserProperties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailAuthCodeIssuedAt",
                table: "AuthUserProperties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailAuthCodePurpose",
                table: "AuthUserProperties",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailAuthCodeAttempts",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "EmailAuthCodeExpiresAt",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "EmailAuthCodeIssuedAt",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "EmailAuthCodePurpose",
                table: "AuthUserProperties");
        }
    }
}
