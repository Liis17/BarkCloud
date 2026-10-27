using BarkCloud.Configuration.Infrastructure;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Configuration.Persistence.Migrations;

/// <summary>
/// Удаляет упразднённые общие секреты (аудит безопасности, п. 9): глобальный HMAC-ключ JWT
/// и вечные сервисные токены *Service:Token — вместе с их значениями в истории изменений.
/// Схема не меняется; подпись JWT теперь RS256 (ключи в файлах), сервисные токены выдаёт Identity.
/// </summary>
[DbContext(typeof(ConfigurationContext))]
[Migration("20261027120000_RemoveLegacySharedSecrets")]
public partial class RemoveLegacySharedSecrets : Migration
{
    private const string SecretKeyCondition = @"(""SettingsTable"" = 'GlobalSettings' AND ""Key"" = 'JwtSettings:SecretKey')
            OR ""Key"" IN ('IdentityService:Token', 'UsersService:Token', 'FilesService:Token', 'TorrentService:Token')";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // История: ревизии ссылаются друг на друга (SourceRevisionId, Restrict) — сначала разрываем
        // цепочки удаляемых ревизий, затем стираем сами ревизии вместе со значениями секретов.
        migrationBuilder.Sql($"""
            UPDATE "SettingsHistory" SET "SourceRevisionId" = NULL
            WHERE "SourceRevisionId" IN (
                SELECT "Id" FROM "SettingsHistory" WHERE {SecretKeyCondition});
            DELETE FROM "SettingsHistory" WHERE {SecretKeyCondition};
            """);

        migrationBuilder.Sql("""
            DELETE FROM "GlobalSettings" WHERE "Key" = 'JwtSettings:SecretKey';
            DELETE FROM "IdentitySettings" WHERE "Key" IN ('IdentityService:Token', 'UsersService:Token');
            DELETE FROM "UsersSettings" WHERE "Key" IN ('IdentityService:Token', 'FilesService:Token');
            DELETE FROM "FilesSettings" WHERE "Key" IN ('IdentityService:Token', 'UsersService:Token');
            DELETE FROM "TorrentSettings" WHERE "Key" IN ('IdentityService:Token', 'UsersService:Token', 'FilesService:Token');
            DELETE FROM "WebSettings" WHERE "Key" IN ('IdentityService:Token', 'UsersService:Token', 'FilesService:Token', 'TorrentService:Token');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Секреты не восстанавливаем: они скомпрометированы общей доступностью и упразднены.
    }
}
