using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    public partial class AddMusicPlaylistsAndAudioMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioAlbum",
                table: "FileMetadata",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioArtist",
                table: "FileMetadata",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioTitle",
                table: "FileMetadata",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioTrackNumber",
                table: "FileMetadata",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicPlaylists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CoverFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylistGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientId = table.Column<long>(type: "bigint", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylistGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylistItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylistShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClickCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylistShareLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistGrants_OwnerId_PlaylistId_RecipientId",
                table: "MusicPlaylistGrants",
                columns: new[] { "OwnerId", "PlaylistId", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistGrants_PlaylistId",
                table: "MusicPlaylistGrants",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistGrants_RecipientId_CreatedAt",
                table: "MusicPlaylistGrants",
                columns: new[] { "RecipientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistItems_FileId",
                table: "MusicPlaylistItems",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistItems_PlaylistId_FileId",
                table: "MusicPlaylistItems",
                columns: new[] { "PlaylistId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistItems_PlaylistId_Position",
                table: "MusicPlaylistItems",
                columns: new[] { "PlaylistId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylists_OwnerId_Name",
                table: "MusicPlaylists",
                columns: new[] { "OwnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylists_OwnerId_UpdatedAt",
                table: "MusicPlaylists",
                columns: new[] { "OwnerId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistShareLinks_OwnerId_CreatedAt",
                table: "MusicPlaylistShareLinks",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistShareLinks_OwnerId_PlaylistId",
                table: "MusicPlaylistShareLinks",
                columns: new[] { "OwnerId", "PlaylistId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistShareLinks_Token",
                table: "MusicPlaylistShareLinks",
                column: "Token",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MusicPlaylistGrants");
            migrationBuilder.DropTable(name: "MusicPlaylistItems");
            migrationBuilder.DropTable(name: "MusicPlaylists");
            migrationBuilder.DropTable(name: "MusicPlaylistShareLinks");

            migrationBuilder.DropColumn(name: "AudioAlbum", table: "FileMetadata");
            migrationBuilder.DropColumn(name: "AudioArtist", table: "FileMetadata");
            migrationBuilder.DropColumn(name: "AudioTitle", table: "FileMetadata");
            migrationBuilder.DropColumn(name: "AudioTrackNumber", table: "FileMetadata");
        }
    }
}
