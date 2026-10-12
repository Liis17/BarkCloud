using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageQuotaGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "StorageProfiles");

            migrationBuilder.CreateTable(
                name: "StorageQuotaGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedBy = table.Column<string>(type: "text", nullable: false),
                    EditedFrom = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageQuotaGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageQuotaGroupBuckets",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    ServiceUrl = table.Column<string>(type: "text", nullable: false),
                    BucketName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageQuotaGroupBuckets", x => new { x.GroupId, x.ServiceUrl, x.BucketName });
                    table.ForeignKey(
                        name: "FK_StorageQuotaGroupBuckets_StorageQuotaGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "StorageQuotaGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageQuotaGroupBuckets_ServiceUrl_BucketName",
                table: "StorageQuotaGroupBuckets",
                columns: new[] { "ServiceUrl", "BucketName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageQuotaGroups_Name",
                table: "StorageQuotaGroups",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageQuotaGroupBuckets");

            migrationBuilder.DropTable(
                name: "StorageQuotaGroups");

            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "StorageProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
