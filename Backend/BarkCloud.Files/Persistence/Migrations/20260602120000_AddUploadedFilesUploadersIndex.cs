using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadedFilesUploadersIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Галерея и подсчёт квоты фильтруют UploadedFiles по "Uploaders".Contains(ownerId),
            // что Npgsql транслирует в "Uploaders" @> ARRAY[ownerId]. Без индекса — seq scan по всей
            // таблице блобов. GIN по bigint[] (array_ops, встроенный) ускоряет оператор @>.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_UploadedFiles_Uploaders\" ON \"UploadedFiles\" USING gin (\"Uploaders\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UploadedFiles_Uploaders\";");
        }
    }
}
