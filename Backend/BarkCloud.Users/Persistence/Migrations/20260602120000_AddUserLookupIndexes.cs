using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Users.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Точный логин/резолв пользователя идёт через lower("Username")/lower("Email")
            // (UsersStorage.GetUserByUsername / GetUserByEmail). Обычный btree-индекс по колонке
            // не задействуется из-за обёртки lower(), поэтому индексируем само выражение.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_Username_Lower\" ON \"Users\" (lower(\"Username\"));");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_UserContacts_Email_Lower\" ON \"UserContacts\" (lower(\"Email\"));");

            // Поиск пользователей (SearchUsers) использует lower(col) LIKE '%q%' по трём полям —
            // leading-wildcard, который btree не ускоряет. Триграммный GIN (pg_trgm) ускоряет подстроку.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_Username_Trgm\" ON \"Users\" USING gin (lower(\"Username\") gin_trgm_ops);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_FirstName_Trgm\" ON \"Users\" USING gin (lower(\"FirstName\") gin_trgm_ops);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_LastName_Trgm\" ON \"Users\" USING gin (lower(\"LastName\") gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Users_LastName_Trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Users_FirstName_Trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Users_Username_Trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UserContacts_Email_Lower\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Users_Username_Lower\";");
        }
    }
}
