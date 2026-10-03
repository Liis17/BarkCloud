using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageCutoverBarrier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageCutovers",
                columns: table => new
                {
                    MigrationId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    SourceServiceUrl = table.Column<string>(type: "text", nullable: false),
                    SourceBucketName = table.Column<string>(type: "text", nullable: false),
                    ProfileIdsJson = table.Column<string>(type: "text", nullable: false),
                    TargetServiceUrl = table.Column<string>(type: "text", nullable: false),
                    TargetBucketName = table.Column<string>(type: "text", nullable: false),
                    TargetConnectionHash = table.Column<string>(type: "text", nullable: false),
                    TargetRegion = table.Column<string>(type: "text", nullable: false),
                    TargetForcePathStyle = table.Column<bool>(type: "boolean", nullable: false),
                    TargetIsR2 = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCutovers", x => x.MigrationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageCutovers_SourceServiceUrl_SourceBucketName",
                table: "StorageCutovers",
                columns: new[] { "SourceServiceUrl", "SourceBucketName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageCutovers");
        }
    }
}
