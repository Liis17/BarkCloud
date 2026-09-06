using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Torrent.Persistence.Migrations
{
    [DbContext(typeof(TorrentContext))]
    [Migration("20260906151000_AddTorrentSearchIndexes")]
    public partial class AddTorrentSearchIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.CreateIndex(
                name: "IX_Torrents_UserId_Name",
                table: "Torrents",
                columns: new[] { "UserId", "Name" });
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Torrents_Name_trgm\" ON \"Torrents\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Torrents_InfoHash_trgm\" ON \"Torrents\" USING gin (\"InfoHash\" gin_trgm_ops);", suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Torrents_InfoHash_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Torrents_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.DropIndex(name: "IX_Torrents_UserId_Name", table: "Torrents");
        }
    }
}
