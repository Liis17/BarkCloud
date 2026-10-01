using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingOtpSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingOtpSecret",
                table: "AuthUserProperties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingOtpSecretExpiresAt",
                table: "AuthUserProperties",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingOtpSecret",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "PendingOtpSecretExpiresAt",
                table: "AuthUserProperties");
        }
    }
}
