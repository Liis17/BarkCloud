using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileMetadata",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TakenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatorTool = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    Altitude = table.Column<double>(type: "double precision", nullable: true),
                    CameraMake = table.Column<string>(type: "text", nullable: true),
                    CameraModel = table.Column<string>(type: "text", nullable: true),
                    LensModel = table.Column<string>(type: "text", nullable: true),
                    FocalLengthMm = table.Column<double>(type: "double precision", nullable: true),
                    FNumber = table.Column<double>(type: "double precision", nullable: true),
                    ExposureTimeSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Iso = table.Column<int>(type: "integer", nullable: true),
                    Orientation = table.Column<int>(type: "integer", nullable: true),
                    Flash = table.Column<bool>(type: "boolean", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    VideoCodec = table.Column<string>(type: "text", nullable: true),
                    AudioCodec = table.Column<string>(type: "text", nullable: true),
                    Bitrate = table.Column<long>(type: "bigint", nullable: true),
                    FrameRate = table.Column<double>(type: "double precision", nullable: true),
                    DocumentAuthor = table.Column<string>(type: "text", nullable: true),
                    DocumentTitle = table.Column<string>(type: "text", nullable: true),
                    DocumentSubject = table.Column<string>(type: "text", nullable: true),
                    DocumentPageCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMetadata", x => x.FileId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileMetadata");
        }
    }
}
