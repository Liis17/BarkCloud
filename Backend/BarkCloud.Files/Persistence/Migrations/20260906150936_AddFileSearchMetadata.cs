using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileSearchMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateTable(
                name: "FileSearchAliases",
                columns: table => new
                {
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileSearchAliases", x => new { x.OwnerId, x.FileId });
                    table.ForeignKey(
                        name: "FK_FileSearchAliases_UploadedFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "UploadedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileTags",
                columns: table => new
                {
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Value = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTags", x => new { x.OwnerId, x.FileId, x.NormalizedValue });
                    table.ForeignKey(
                        name: "FK_FileTags_UploadedFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "UploadedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileSearchAliases_FileId",
                table: "FileSearchAliases",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileSearchAliases_OwnerId_NormalizedValue",
                table: "FileSearchAliases",
                columns: new[] { "OwnerId", "NormalizedValue" });

            migrationBuilder.CreateIndex(
                name: "IX_FileTags_FileId",
                table: "FileTags",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileTags_OwnerId_NormalizedValue",
                table: "FileTags",
                columns: new[] { "OwnerId", "NormalizedValue" });

            migrationBuilder.Sql("CREATE INDEX \"IX_FileSearchAliases_NormalizedValue_trgm\" ON \"FileSearchAliases\" USING gin (\"NormalizedValue\" gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX \"IX_FileTags_NormalizedValue_trgm\" ON \"FileTags\" USING gin (\"NormalizedValue\" gin_trgm_ops);");

            // Индексы на уже заполненных таблицах создаём без долгой блокировки записи.
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_CloudFileEntries_Name_trgm\" ON \"CloudFileEntries\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_UploadedFiles_Filename_trgm\" ON \"UploadedFiles\" USING gin (\"Filename\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_CloudDirectories_Name_trgm\" ON \"CloudDirectories\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Albums_Name_trgm\" ON \"Albums\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Albums_Description_trgm\" ON \"Albums\" USING gin (\"Description\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_MusicPlaylists_Name_trgm\" ON \"MusicPlaylists\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_MusicPlaylists_Description_trgm\" ON \"MusicPlaylists\" USING gin (\"Description\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_DynamicFolders_Name_trgm\" ON \"DynamicFolders\" USING gin (\"Name\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_DocumentTitle_trgm\" ON \"FileMetadata\" USING gin (\"DocumentTitle\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_DocumentAuthor_trgm\" ON \"FileMetadata\" USING gin (\"DocumentAuthor\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_DocumentSubject_trgm\" ON \"FileMetadata\" USING gin (\"DocumentSubject\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_AudioTitle_trgm\" ON \"FileMetadata\" USING gin (\"AudioTitle\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_AudioArtist_trgm\" ON \"FileMetadata\" USING gin (\"AudioArtist\" gin_trgm_ops);", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_FileMetadata_AudioAlbum_trgm\" ON \"FileMetadata\" USING gin (\"AudioAlbum\" gin_trgm_ops);", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_AudioAlbum_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_AudioArtist_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_AudioTitle_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_DocumentSubject_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_DocumentAuthor_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_FileMetadata_DocumentTitle_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_DynamicFolders_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_MusicPlaylists_Description_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_MusicPlaylists_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Albums_Description_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Albums_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_CloudDirectories_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_UploadedFiles_Filename_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_CloudFileEntries_Name_trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_FileTags_NormalizedValue_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_FileSearchAliases_NormalizedValue_trgm\";");
            migrationBuilder.DropTable(
                name: "FileSearchAliases");

            migrationBuilder.DropTable(
                name: "FileTags");
        }
    }
}
