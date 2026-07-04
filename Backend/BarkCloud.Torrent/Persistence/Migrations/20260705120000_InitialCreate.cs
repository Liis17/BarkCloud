using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Torrent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Torrents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    InfoHash = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MagnetUri = table.Column<string>(type: "text", nullable: true),
                    TorrentFile = table.Column<byte[]>(type: "bytea", nullable: true),
                    SavePath = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalSize = table.Column<long>(type: "bigint", nullable: false),
                    Downloaded = table.Column<long>(type: "bigint", nullable: false),
                    Uploaded = table.Column<long>(type: "bigint", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Torrents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TorrentFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TorrentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TorrentFiles_Torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "Torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TorrentFiles_TorrentId",
                table: "TorrentFiles",
                column: "TorrentId");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_UserId",
                table: "Torrents",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TorrentFiles");

            migrationBuilder.DropTable(
                name: "Torrents");
        }
    }
}
