using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TotpReplayAndReauthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReauthPermissionId",
                table: "WebAuthnChallenges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SecurityStateVersion",
                table: "WebAuthnChallenges",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SessionId",
                table: "WebAuthnChallenges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SecurityStateVersion",
                table: "UserPasswords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "EmailAuthCodeRequestId",
                table: "AuthUserProperties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastAcceptedTotpStep",
                table: "AuthUserProperties",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OtpSecretGeneration",
                table: "AuthUserProperties",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PendingOtpSecurityStateVersion",
                table: "AuthUserProperties",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ReauthPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    CredentialId = table.Column<long>(type: "bigint", nullable: true),
                    SecurityStateVersion = table.Column<long>(type: "bigint", nullable: false),
                    PermissionTokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WebAuthnChallengeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReauthPermissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReauthPermissions_ExpiresAt",
                table: "ReauthPermissions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReauthPermissions_PermissionTokenHash",
                table: "ReauthPermissions",
                column: "PermissionTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReauthPermissions");

            migrationBuilder.DropColumn(
                name: "ReauthPermissionId",
                table: "WebAuthnChallenges");

            migrationBuilder.DropColumn(
                name: "SecurityStateVersion",
                table: "WebAuthnChallenges");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "WebAuthnChallenges");

            migrationBuilder.DropColumn(
                name: "SecurityStateVersion",
                table: "UserPasswords");

            migrationBuilder.DropColumn(
                name: "EmailAuthCodeRequestId",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "LastAcceptedTotpStep",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "OtpSecretGeneration",
                table: "AuthUserProperties");

            migrationBuilder.DropColumn(
                name: "PendingOtpSecurityStateVersion",
                table: "AuthUserProperties");
        }
    }
}
