using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoVariantsAndTranscodeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoTranscodeLeases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoTranscodeLeases", x => x.Id);
                    table.CheckConstraint("CK_VideoTranscodeLeases_SingletonId", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "VideoTranscodeStates",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SkippedHeights = table.Column<List<int>>(type: "integer[]", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PendingVariantFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoTranscodeStates", x => x.FileId);
                });

            migrationBuilder.CreateTable(
                name: "VideoVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    ActualHeight = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoVariants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoTranscodeStates_Status_UpdatedAt",
                table: "VideoTranscodeStates",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoVariants_OriginalFileId_Height",
                table: "VideoVariants",
                columns: new[] { "OriginalFileId", "Height" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoVariants_VariantFileId",
                table: "VideoVariants",
                column: "VariantFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoTranscodeLeases");

            migrationBuilder.DropTable(
                name: "VideoTranscodeStates");

            migrationBuilder.DropTable(
                name: "VideoVariants");
        }
    }
}
